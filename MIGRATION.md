# Migrasi Data Logger C++ ke C#

## Dasar perubahan

- Repo: https://github.com/arhan321/collector_windows
- Commit dasar: `4830eebc070b4e4e70a0ae8eb861e257fde559ec` pada `master`.
- Runtime: .NET 10 LTS; Windows x64 untuk pembacaan nyata.
- SDK: YMConnectDotNet Windows 1.1.10. API adapter diperiksa terhadap metadata DLL
  resmi dan dikompilasi dengan referensi DLL tersebut.
- Tidak ada NuGet pihak ketiga. HTTP, JSON, task dan filesystem memakai library .NET.

Kode C++ lama tetap ada di repo untuk referensi. Source ZIP membawa proyek C# aktif,
SDK .NET dan dokumentasi. Patch dapat diterapkan pada checkout terpisah dari commit
dasar; tidak membawa token pribadi, log data, queue produksi atau build lama.

Untuk mulai paling mudah, ekstrak source ZIP ke folder baru dan buka folder yang
berisi `RmsCollector.csproj` di VS Code. Source ZIP adalah proyek mandiri.

Jika ingin menerapkan ke repo lama, buat checkout atau branch terpisah pada
commit dasar di atas, lalu jalankan dari root repo:

```powershell
git apply --check "C:\lokasi\collector_windows_csharp.patch"
git apply "C:\lokasi\collector_windows_csharp.patch"
```

Patch binary tersedia di dalam source ZIP. Jika pemeriksaan gagal karena README
atau file lain telah berubah, gunakan folder source mandiri dahulu dan tinjau
perbedaannya; jangan memaksa overwrite. Repo GitHub belum diperbarui dari paket ini.

## Kontrak yang dipertahankan

| Endpoint | Isi |
|---|---|
| `/api/v1/collector/connections` | robot_id, state, occurred_at UTC, ip_address dan simulated |
| `/api/v1/collector/telemetry` | UUID sesi, sequence, sampled_at UTC, status read, enam axis SLURBT, sdk_raw dan metadata |
| `/api/v1/collector/operating-times` | Empat time_type, value_raw, value_seconds diagnostik dan simulated |
| `/api/v1/collector/alarms/snapshots` | sdk_text, alarm aktif, lima kategori history, status dan simulated |

Field dicocokkan dengan controller API di
`arhan321/laravel_yaskawa_sqlserv/src/app/Http/Controllers/Api`. ID robot dan token
tetap milik lingkungan Laravel. Collector tidak menulis SQL secara langsung.

## Perubahan perilaku yang disengaja

- DLL native hanya dimuat saat check-sdk atau real. Mock/probe tidak memerlukannya.
- Check-sdk membantu memeriksa pasangan DLL dan runtime tanpa menghubungi robot.
- Config menerima komentar # dan //, termasuk contoh delapan robot lama.
- HTTPS didukung; redirect dinonaktifkan dan proxy sistem dilewati untuk API lokal/LAN.
- Backlog dikirim lebih dulu. Sample baru masuk antrean agar tidak mendahului sample
  lama ketika API pulih. Semua payload replay dipertahankan, termasuk timestamp/UUID.
- Satu proses C# memiliki lock spool per robot. Collector C++ tidak mengikuti lock
  ini; hentikan versi lama sebelum menjalankan versi baru.
- File queue atomik di-flush ke disk. Pembatalan request mempertahankan payload.
- Queue dibatasi 1 MiB per file dan robot_id diperiksa sebelum replay; endpoint
  dibatasi ke empat endpoint collector. File korup tidak dikirim/dihapus diam-diam.
- Body respons HTTP maksimal 64 KiB dan diagnostik maksimal 4096 karakter.

## Verifikasi

Perintah: `dotnet run --project tests/RmsCollector.Tests.csproj -c Release`.
Harness tanpa framework test eksternal mencakup guard CLI, validasi config, kontrak
JSON, null/zero semantics, empat counter, Unicode, outage/FIFO replay, penolakan
permanen, quarantine queue lama, file korup, pembatalan, lock spool, antrean penuh,
dry-run tanpa side effect dan reconnect setelah tiga pembacaan gagal.

Mock C++ lama dibandingkan secara semantik dengan C#: tujuh payload per siklus
pada empat endpoint. Waktu pengambilan dan UUID sesi berbeda secara alami; field
lain dibandingkan dengan toleransi floating point. SHA256 archive SDK dicocokkan
dengan metadata rilis resmi.

Windows x64 self-contained dibuat dengan cross-publish. Belum dijalankan pada
Windows atau robot fisik dalam lingkungan pengerjaan ini. Uji check-sdk, real once
dry-run, lalu POST pada Windows DOJO sebelum penggunaan kontinu.

Tidak ada perubahan Ignition/OPC UA atau aturan bukti operating time PM dalam migrasi.
