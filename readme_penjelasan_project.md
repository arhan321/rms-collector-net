# RMS Data Logger C# untuk Windows

Data Logger aktif menggunakan **C# dan .NET 10 LTS**. Program membaca robot melalui
**YMConnect .NET resmi**, lalu mengirim JSON ke API Laravel yang sudah ada.
Komunikasi robot tetap Ethernet/UDP; perubahan ini tidak menambahkan OPC UA.

**Tidak perlu MSVC Build Tools, Visual Studio penuh, CMake, NMake, libcurl atau vcpkg
untuk build C# ini.** DLL native YMConnect tetap memerlukan **Visual C++ v14
Redistributable x64**. Redistributable adalah runtime, bukan compiler/Build Tools.

Panduan lengkap: **[PANDUAN_WINDOWS.md](PANDUAN_WINDOWS.md)**.

## Mulai di laptop

1. Pasang [.NET 10 SDK Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
   pada bagian **Build apps – SDK**, lalu buka terminal baru.
2. Pasang [Visual C++ v14 Redistributable x64](https://aka.ms/vc14/vc_redist.x64.exe)
   jika pemeriksaan SDK belum berhasil. Ini bukan instalasi Visual Studio.
3. Buka PowerShell di folder yang berisi `RmsCollector.csproj` dan jalankan:

```powershell
dotnet --version
dotnet build .\RmsCollector.csproj -c Release
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --mock --once --dry-run --config .\config\collector.dojo.conf
```

Mock dry-run ini tidak terhubung ke robot, tidak mengirim HTTP dan tidak perlu token.
Tidak perlu Native Tools Command Prompt. Pasangan DLL .NET resmi 1.1.10 sudah
tersedia di `sdk/dotnet` dan disalin otomatis saat build/publish.

## Periksa SDK dan API

```powershell
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --check-sdk
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --probe
```

`--check-sdk` hanya memeriksa pemuatan DLL, tanpa menghubungi robot. `--probe`
hanya melakukan `GET /up` ke API. Keduanya belum membuktikan data tersimpan di DB.

## Uji pembacaan nyata

Pastikan `robot=` memakai ID database yang benar, IP controller dapat dijangkau,
dan backend berjalan. Siapkan token Laravel dalam satu baris tanpa awalan Bearer:

```powershell
Copy-Item .\collector.token.example .\collector.token
notepad .\collector.token
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --real --once --dry-run
```

Perintah terakhir membaca satu siklus, tetapi tidak mengirim ke Laravel. Setelah
hasilnya sesuai, lakukan satu siklus dengan POST, lalu jalankan kontinu:

```powershell
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --real --once
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --real
```

Hentikan dengan **Ctrl+C**. Token contoh ditolak sampai diganti token asli.
Jangan menjalankan collector C++ dan C# bersamaan untuk robot/spool yang sama.

## Paket offline untuk DOJO

Jalankan saat laptop memiliki koneksi. Pemakaian pertama bisa mengunduh runtime
pack Windows yang dibutuhkan untuk publish, bukan workload compiler C++:

```powershell
dotnet publish .\RmsCollector.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Salin **seluruh isi** `publish\win-x64` ke folder tetap, misalnya `C:\RMS-Collector`.
Paket membawa .NET Runtime. DOJO hanya perlu runtime Visual C++ x64 untuk DLL
robot jika belum ada. Cek config dan buat `collector.token` di folder tujuan:

```powershell
Set-Location -LiteralPath 'C:\RMS-Collector'
.\rms_collector.exe --mock --once --dry-run --config .\config\collector.dojo.conf
.\rms_collector.exe --check-sdk
.\rms_collector.exe --config .\config\collector.dojo.conf --probe
.\rms_collector.exe --config .\config\collector.dojo.conf --real --once --dry-run
.\rms_collector.exe --config .\config\collector.dojo.conf --real
```

Jika ingin mengedit dan build di DOJO, pasang .NET 10 SDK di DOJO dan gunakan source.
SDK sudah termasuk runtime. Build biasa tidak memerlukan NuGet pihak ketiga;
publish untuk RID tertentu memerlukan runtime pack yang harus disiapkan online.
Jangan aktifkan Native AOT atau C++/CLI; keduanya bukan jalur build proyek ini.

## Fungsi yang dipertahankan

- Config `key=value`, `robot=id,ip,R1,io;io`, `--robot-id` dan interval polling.
- Posisi pulse dan torque mentah axis S L U R B T; read gagal tetap null.
- State setiap siklus telemetry; job, informasi sistem dan IO pada extras.
- Empat operating counters, alarm aktif dan lima kategori history, 20 per kategori.
- Empat endpoint Laravel; UUID sesi, sequence dan timestamp UTC untuk replay.
- Queue dua baris kompatibel dengan `.queue` lama, retry, quarantine HTTP 422,
  berhenti pada penolakan permanen dan antrean penuh.
- Mock POST diblokir kecuali `--allow-mock-post` untuk ID robot DEMO.
- Read-only: tidak memerintah gerakan, servo on/off, start job atau IO write.

Maksimum pilot **8 robot**, sama dengan collector lama. Target 32 robot perlu
pengujian beban. Nilai operating time tetap tunduk pada verifikasi bukti di backend;
migrasi tidak otomatis mengesahkan nilai PM.

## Pengujian

```powershell
dotnet run --project .\tests\RmsCollector.Tests.csproj -c Release
```

Lihat [MIGRATION.md](MIGRATION.md) untuk hasil verifikasi dan perubahan antrean.
Di repo, `CMakeLists.txt`, `src/*.cpp` dan SDK C++ lama dipertahankan sebagai referensi,
tetapi **tidak digunakan** oleh proyek C#. Source ZIP hanya membawa proyek C# aktif.
