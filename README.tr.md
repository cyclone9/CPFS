[English](README.md) · [فارسی](README.fa.md) · [العربية](README.ar.md) · [Türkçe](README.tr.md)

# CPFS - CyClone Power File Share

**Slogan:** *Bilgisayarınızdaki klasör, her cihazın erişiminde.*  
**Moto:** *Yalnızca seçtiğiniz klasör.*

| | |
|---|---|
| Sürüm | 0.5.0 (geliştirme aşamasında) |
| Platform | Windows 10 ve 11 (Ev veya Pro sürümü) |
| Gereksinimler | .NET Framework 4.8 (Windows 10 1903 ve sonrası ile Windows 11'de yerleşik gelir), Yönetici hakları |
| Ana sayfa | https://cyclone9.ir/cpfs.html |
| Lisans | MIT (yalnızca kod; bkz. [Lisans](#lisans)) |

CPFS, Windows uygulaması olarak yeniden yazılıyor. Henüz yayımlanmış bir sürüm yok, yani indirilecek bir şey de yok; aşağıda anlatılanların hepsi uygulamanın yapması hedeflenen şeylerdir.

## CPFS ne yapar

CPFS, bir Windows 10 veya 11 bilgisayarındaki tek bir klasörü Windows, macOS, Linux, Android ve iOS cihazlarla paylaşır. Cihazlar bilgisayarın adıyla (`ComputerName.local`), Wi-Fi veya LAN adresiyle ya da bir VPN adresiyle bağlanır; telefon QR koduyla da bağlanabilir. Paylaşım yalnızca o klasörle sınırlıdır, ayrı bir hesap ve sertleştirilmiş Windows ayarları kullanılır; Kaldırma özgün ayarları geri getirir. Bir Linux sunucunuz varsa CPFS, tek bir alt klasörü onunla şifreli bir kanal üzerinden eşitleyebilir. Bir tarafta silinen veya değiştirilen dosya, diğer tarafta sürümler klasöründe saklanır.

## Uygulamada neler olacak

- Kurulum, İzleme, Eşitleme, Bağlanma ve Kaldırma sayfaları.
- Sistem, Açık ve Koyu temalı Windows 11 görünümü.
- Önce İngilizce, ardından Farsça, Arapça ve Türkçe.
- Sürücü harfini, sürücü adını ve kısayol adını kendiniz seçersiniz.
- Windows, macOS, Linux, Android ve iOS için QR kodlu bağlantı kılavuzu; `.local` adı, Wi-Fi veya LAN IP adresi ya da VPN adresi üzerinden.
- Paylaşımın tek bir alt klasörünün bir Linux sunucusuyla, şifreli bir kanal üzerinden isteğe bağlı eşitlenmesi.
- Özgün Windows ayarlarını geri yükleyen Kaldırma.
- Dosyalarınız silinmez. CPFS, paylaşılan klasördeki hiçbir şeyi kendiliğinden silmez, taşımaz ya da üzerine yazmaz. Eşitleme açıkken bir tarafta silinen veya değiştirilen dosya, diğer tarafta silinmeden bir sürümler klasöründe tutulur.
- Önceki CPFS betikleriyle yapılmış kurulumlar algılanır.

## Lisans

Kod MIT lisansı altındadır (bkz. `LICENSE`). CPFS adı, logo, semboller, yazı logosu ve uygulama simgesi bu lisansın kapsamında değildir; izinsiz kullanılamaz. `assets/brand/` içindeki dosyalar CPFS marka dosyalarıdır ve MIT lisansının kapsamında değildir. `assets/fonts/` içindeki Vazirmatn yazı tipi SIL Open Font License 1.1 altındadır (`OFL.txt` orada).

© CyClone · https://cyclone9.ir/
