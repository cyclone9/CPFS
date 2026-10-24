[English](README.md) · [فارسی](README.fa.md) · [العربية](README.ar.md) · [Türkçe](README.tr.md)

# CPFS - CyClone Power File Share

<p align="center"><picture><source media="(prefers-color-scheme: dark)" srcset="assets/brand/Logo/cpfs-lockup-on-dark.svg"><img src="assets/brand/Logo/cpfs-lockup-on-light.svg" alt="CPFS" width="348"></picture></p>

**Slogan:** *Bilgisayarınızdaki klasör, her cihazın erişiminde.*  
**Moto:** *Yalnızca seçtiğiniz klasör.*

| | |
|---|---|
| Sürüm | 0.7.0 (geliştirme aşamasında) |
| Platform | Windows 10 ve 11 (Ev veya Pro sürümü) |
| Gereksinimler | .NET Framework 4.8 (Windows 10 1903 ve sonrası ile Windows 11'de yerleşik gelir), Yönetici hakları |
| Ana sayfa | https://cyclone9.ir/cpfs |
| Lisans | MIT (yalnızca kod; bkz. [Lisans](#lisans)) |

<p align="center"><img src="assets/brand/cpfs-video-en.jpg" alt="CPFS" width="720"></p>

CPFS, Windows uygulaması olarak yeniden yazılıyor. Henüz yayımlanmış bir sürüm yok, yani indirilecek bir şey de yok; aşağıda anlatılanların hepsi uygulamanın yapması hedeflenen şeylerdir.

## CPFS ne yapar

CPFS, bir Windows 10 veya 11 bilgisayarındaki tek bir klasörü Windows, macOS, Linux, Android ve iOS cihazlarla paylaşır. Cihazlar bilgisayarın adıyla (`ComputerName.local`), Wi-Fi veya LAN adresiyle ya da bir VPN adresiyle bağlanır; telefon QR koduyla da bağlanabilir. Paylaşım yalnızca o klasörle sınırlıdır, ayrı bir hesap ve sertleştirilmiş Windows ayarları kullanılır; Kaldırma özgün ayarları geri getirir.

## Uygulamada neler olacak

- Kurulum, İzleme, Bağlanma ve Kaldırma sayfaları.
- Sistem, Açık ve Koyu temalı Windows 11 görünümü.
- Önce İngilizce, ardından Farsça, Arapça ve Türkçe.
- Kurulum, paylaşılan klasöre bir sürücü harfi, bir masaüstü kısayolu ve Dosya Gezgini'nin gezinti bölmesinde bir giriş verebilir. Harfi ve adları siz seçersiniz; üçünde de CPFS simgesi bulunur. Sürücü harfinde Gezgin, sizin adınız yerine diskin kendi adını gösterebilir. Gezinti bölmesi girişi, varsayılan olarak açık olan "Dosya Gezgini'nde göster" anahtarıdır.
- Windows, macOS, Linux, Android ve iOS için bağlantı kılavuzu, telefonlar için QR kodlarıyla; `.local` adı, Wi-Fi veya LAN IP adresi ya da VPN adresi üzerinden.
- İzleme, bağlı cihazları ve trafiklerini gösterir; o anda kopyalanan büyük aktarımları ("Şu anda kopyalananlar"), Son etkinliği ve başarısız oturum açma denemelerini de listeler. Son etkinlik, paylaşılan klasörde eklenen, değiştirilen, yeniden adlandırılan veya silinen her dosyayı gösterir; değişiklik herhangi bir cihazdan ya da bu bilgisayardan gelmiş olabilir. CPFS değişikliğin hangi cihazdan geldiğini söyleyemez.
- Son etkinlik için isteğe bağlı "Günlük dosyasına kaydet" anahtarı; varsayılan olarak kapalıdır.
- Hakkında sayfasında "Güncellemeleri denetle" düğmesi ve CPFS açılırken isteğe bağlı denetim; varsayılan olarak kapalıdır. Yalnızca GitHub'a sorar, hiçbir şey indirmez.
- Özgün Windows ayarlarını geri yükleyen Kaldırma.
- Dosyalarınız silinmez. CPFS, paylaşılan klasördeki hiçbir şeyi kendiliğinden silmez, taşımaz ya da üzerine yazmaz.
- Önceki CPFS betikleriyle yapılmış kurulumlar algılanır.

## Kaldırma

Kaldırma, Kurulum'un değiştirdiği Windows ayarlarını geri getirir ve CPFS'nin oluşturduklarını siler. Paylaşılan klasör ve içindeki dosyalar yerinde kalır. Hızlı erişim öğeleri şöyle ele alınır:

- Dosya Gezgini girişi, hâlâ paylaşılan klasörü açıyorsa silinir. Giriş `HKCU\Software\Classes\CLSID\{07C33AB4-B371-467B-9DC1-1AA530E251C6}` ve `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{07C33AB4-B371-467B-9DC1-1AA530E251C6}` anahtarlarından, ayrıca `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel` altındaki `{07C33AB4-B371-467B-9DC1-1AA530E251C6}` değerinden oluşur; hepsi Kurulum'u çalıştıran kullanıcının kovanındadır.
- Sürücü simgesi (`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\DriveIcons\<letter>\DefaultIcon`) silinir ya da harfin önceden sahip olduğu simge geri konur. CPFS'nin koymadığı bir simge yerinde kalır.
- `C:\ProgramData\CPFS\cpfs.ico` dosyası, `C:\ProgramData\CPFS` klasörünün geri kalanıyla birlikte silinir.

## CPFS verileri nerede tutar

- `C:\ProgramData\CPFS` klasörü: yanıtlarınızı ve özgün ayarların yedeğini içeren `config.json` ile sürücünün, kısayolun ve Dosya Gezgini girişinin kullandığı CPFS simgesi `cpfs.ico`.
- CPFS'yi çalıştıran kullanıcının profilindeki `%LOCALAPPDATA%\CPFS` klasörü:
  - `preferences.json`: uygulamadaki seçimleriniz; tema, dil ve yazı tipi gibi.
  - `devices.json`: bağlı cihazlara verdiğiniz adlar.
  - `cpfs-log.txt`: uygulamanın kendi günlüğü.
  - `activity-log.txt` ve `activity-log.old.txt`: Son etkinlik; yalnızca "Günlük dosyasına kaydet" açıkken yazılır. Dosya 5 MB'a ulaşınca `activity-log.old.txt` olur. Dosya adları içerdiği için Raporu kopyala'ya hiçbir zaman eklenmez. Kaldırma onu silmez, çünkü size ait bir günlüktür.

Kaldırma, `%LOCALAPPDATA%\CPFS` içinde hiçbir şeyi silmez.

## Lisans

Kod MIT lisansı altındadır (bkz. `LICENSE`). CPFS adı, logo, semboller, yazı logosu ve uygulama simgesi bu lisansın kapsamında değildir; izinsiz kullanılamaz. `assets/brand/` içindeki dosyalar CPFS marka dosyalarıdır ve MIT lisansının kapsamında değildir. `assets/fonts/` içindeki Vazirmatn yazı tipi SIL Open Font License 1.1 altındadır (`OFL.txt` orada).

© CyClone · https://cyclone9.ir/
