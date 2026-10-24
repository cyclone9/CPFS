using System;
using System.Collections.Generic;
using Cpfs.Core.Planning;

namespace Cpfs.Core.Apply;

public enum StepOutcome
{
    Pending,
    Running,
    Done,

    // Already as planned when read again: nothing written.
    Kept,

    // Belongs to a later version (Auto-Private, computer name); not run.
    Deferred,

    // Windows refused the call as not supported on this system: not done, and the run goes on.
    Unsupported,

    // Done for some of its items and not for others (file ownership): the run goes on.
    Partial,
    Failed,

    // After a failure: not reached.
    NotRun,
}

public sealed record StepReport(int Index, string Key, StepOutcome Outcome, string? Error = null);

// One change on Windows. The executor calls, in order: ReadIsAtTarget, Record, Apply, Track, Verify.
public interface IChangeStep
{
    // Reads the current state again; true when it already matches the plan, and nothing is written.
    bool ReadIsAtTarget();

    // Writes what Uninstall needs before the change (an original value, a removed object's definition, an entry
    // CPFS is about to create), so a failure part-way still leaves it recorded.
    BackupWrite Record(ConfigBook? book);

    void Apply();

    // Writes what the change produced into config.json (the share's name, the account's name).
    void Track(ConfigBook? book);

    // Reads the state once more; false when the change did not take effect.
    bool Verify();
}

// Windows refused a call as not supported (an SMB server setting this build does not take): reported, not fatal.
public sealed class StepUnsupportedException : Exception
{
    public StepUnsupportedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

// A step that did part of its work: reported with its counts, not fatal. A step that did none of it throws an
// ordinary exception and stops the run.
public sealed class StepPartialException : Exception
{
    public StepPartialException(string message)
        : base(message)
    {
    }
}

public sealed record ApplyEntry(PlanItem Item, bool Deferred);

// Which plan items Apply runs, and in what order.
public static class ApplyList
{
    // Setup's items that only carry a choice for other items (the ticked VPN adapter feeds the VPN rule).
    private static readonly string[] ChoiceOnlyPrefixes = { "fw.vpnadapter:" };

    public static bool IsRunnable(PlanItem item) => item.Action switch
    {
        PlanAction.Create or PlanAction.Change or PlanAction.RemoveIfOurs => true,
        PlanAction.Ask => item.Ticked,
        _ => false,
    };

    // Auto-Private and the computer name come in a later version.
    public static bool IsDeferred(PlanItem item) =>
        item.Section is PlanSection.AutoPrivate or PlanSection.Computer;

    // Items that stop Apply: anything that could not be planned, except in the sections that are not applied yet.
    public static IReadOnlyList<PlanItem> Blocking(Plan plan)
    {
        var list = new List<PlanItem>();
        foreach (var item in plan.Items)
        {
            if (item.Action == PlanAction.CannotPlan && !IsDeferred(item))
            {
                list.Add(item);
            }
        }

        return list;
    }

    // The data folder is locked first (config.json lives in it), then the backup is taken, then the rest in the
    // planner's order.
    public static IReadOnlyList<ApplyEntry> Setup(Plan plan)
    {
        var first = new List<ApplyEntry>();
        var rest = new List<ApplyEntry>();
        foreach (var item in plan.Items)
        {
            if (!IsRunnable(item) || IsChoiceOnly(item.Key))
            {
                continue;
            }

            var entry = new ApplyEntry(item, IsDeferred(item));
            if (item.Key == "data.folder")
            {
                first.Insert(0, entry);
            }
            else if (item.Key == "backup")
            {
                first.Add(entry);
            }
            else
            {
                rest.Add(entry);
            }
        }

        first.AddRange(rest);
        return first;
    }

    // Uninstall keeps the planner's order: the data folder is removed last.
    public static IReadOnlyList<ApplyEntry> Uninstall(Plan plan)
    {
        var list = new List<ApplyEntry>();
        foreach (var item in plan.Items)
        {
            if (IsRunnable(item) && !UninstallPlanner.IsChoiceOnly(item.Key))
            {
                list.Add(new ApplyEntry(item, IsDeferred(item)));
            }
        }

        return list;
    }

    private static bool IsChoiceOnly(string key)
    {
        foreach (var prefix in ChoiceOnlyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class ExecutionResult
{
    public ExecutionResult(IReadOnlyList<StepReport> reports, string? failedKey, string? error)
    {
        Reports = reports;
        FailedKey = failedKey;
        Error = error;
    }

    public IReadOnlyList<StepReport> Reports { get; }

    public bool Completed => FailedKey is null;

    public string? FailedKey { get; }

    public string? Error { get; }

    // Items Windows refused or did only in part: the run finished, but not everything planned is in place.
    public int NotDone => Count(StepOutcome.Unsupported) + Count(StepOutcome.Partial);

    public int Count(StepOutcome outcome)
    {
        var n = 0;
        foreach (var r in Reports)
        {
            if (r.Outcome == outcome)
            {
                n++;
            }
        }

        return n;
    }
}

// Runs the steps one by one and stops at the first failure. config.json is saved after every step that recorded or
// changed something, so whatever was done stays recorded for Uninstall even when a later step fails.
public static class Executor
{
    public static ExecutionResult Run(
        IReadOnlyList<ApplyEntry> entries,
        Func<ApplyEntry, IChangeStep> stepFor,
        ConfigBook? book,
        Action<ConfigBook>? save,
        Action<StepReport>? progress,
        Action<ConfigBook>? onSuccess = null)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        if (stepFor is null)
        {
            throw new ArgumentNullException(nameof(stepFor));
        }

        var reports = new List<StepReport>();
        void Report(StepReport r)
        {
            if (r.Outcome != StepOutcome.Running)
            {
                reports.Add(r);
            }

            progress?.Invoke(r);
        }

        void Save()
        {
            if (book is not null && save is not null && book.CanSave)
            {
                save(book);
            }
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var key = entry.Item.Key;
            if (entry.Deferred)
            {
                Report(new StepReport(i, key, StepOutcome.Deferred));
                continue;
            }

            Report(new StepReport(i, key, StepOutcome.Running));
            var dirty = false;
            try
            {
                var step = stepFor(entry);
                if (step.ReadIsAtTarget())
                {
                    Report(new StepReport(i, key, StepOutcome.Kept));
                    continue;
                }

                var recorded = step.Record(book);
                if (recorded == BackupWrite.NoBackup && entry.Item.BackupNeeded)
                {
                    throw new InvalidOperationException("The original value could not be recorded: config.json has no backup to add it to.");
                }

                if (recorded == BackupWrite.Recorded)
                {
                    Save();
                }

                dirty = true;
                step.Apply();
                step.Track(book);
                Save();
                dirty = false;
                if (!step.Verify())
                {
                    throw new InvalidOperationException("The change did not take effect: Windows still reports the old value.");
                }

                Report(new StepReport(i, key, StepOutcome.Done));
            }
            catch (StepUnsupportedException ex)
            {
                if (dirty)
                {
                    TrySave(Save);
                }

                Report(new StepReport(i, key, StepOutcome.Unsupported, ex.Message));
            }
            catch (StepPartialException ex)
            {
                if (dirty)
                {
                    TrySave(Save);
                }

                Report(new StepReport(i, key, StepOutcome.Partial, ex.Message));
            }
            catch (Exception ex)
            {
                if (dirty)
                {
                    TrySave(Save);
                }

                var error = Describe(ex);
                Report(new StepReport(i, key, StepOutcome.Failed, error));
                for (var j = i + 1; j < entries.Count; j++)
                {
                    reports.Add(new StepReport(j, entries[j].Item.Key, StepOutcome.NotRun));
                }

                return new ExecutionResult(reports, key, error);
            }
        }

        if (book is not null)
        {
            onSuccess?.Invoke(book);
            book.MarkFinished();
            Save();
        }

        return new ExecutionResult(reports, null, null);
    }

    private static void TrySave(Action save)
    {
        try
        {
            save();
        }
        catch (Exception)
        {
            // The step's own error is the one to show; a second failure to save would hide it.
        }
    }

    private static string Describe(Exception ex)
    {
        var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
        return inner.GetType().Name + ": " + inner.Message;
    }
}

// A step built from functions, for the Windows steps and for tests.
public sealed class LambdaStep : IChangeStep
{
    public Func<bool> Read { get; init; } = () => false;

    public Func<ConfigBook?, BackupWrite> RecordWith { get; init; } = _ => BackupWrite.NotNeeded;

    public Action Do { get; init; } = () => { };

    public Action<ConfigBook?> TrackWith { get; init; } = _ => { };

    public Func<bool> Check { get; init; } = () => true;

    public bool ReadIsAtTarget() => Read();

    public BackupWrite Record(ConfigBook? book) => RecordWith(book);

    public void Apply() => Do();

    public void Track(ConfigBook? book) => TrackWith(book);

    public bool Verify() => Check();
}
