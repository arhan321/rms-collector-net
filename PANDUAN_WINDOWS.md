# Panduan instalasi dan menjalankan RMS C#

## 1 Pilih kebutuhan perangkat

| Kebutuhan | Yang dipasang atau dibawa |
|---|---|
| Laptop untuk edit dan build | .NET 10 SDK Windows x64, editor seperti VS Code, SDK YMConnect yang sudah ada dalam source |
| DOJO menjalankan paket siap pakai | Seluruh folder self-contained dan Visual C++ v14 Redistributable x64 jika belum ada |
| DOJO untuk edit dan build | .NET 10 SDK dan source; siapkan runtime pack dulu jika ingin publish offline |

**MSVC Build Tools tidak digunakan pada jalur C# biasa ini.** Runtime Visual C++ x64
masih diperlukan karena library robot berupa DLL native vendor. XAMPP dan SQL
Server bukan dependensi build C#; keduanya dipakai backend. Jangan memilih proyek
`.NET Framework` atau mengaktifkan Native AOT.

## 2 Instal .NET di laptop

1. Buka https://dotnet.microsoft.com/en-us/download/dotnet/10.0
2. Pilih **Build apps – SDK**, kemudian **Windows x64 Installer**. SDK 10.0.403
   yang dicek saat penyiapan sekitar 215 MB; ukuran versi lain bisa berbeda.
3. Jalankan installer sampai selesai. Tutup dan buka kembali PowerShell atau
   terminal VS Code. Tidak perlu membuka Native Tools Command Prompt.
4. Jalankan `dotnet --version` dan `dotnet --list-sdks`. Harus ada SDK **10.0.x**.
5. Ekstrak seluruh source ZIP. Jalankan perintah dari folder yang berisi
   `RmsCollector.csproj`, bukan dari System32.

Jika `dotnet` tidak dikenali, buka terminal baru setelah instalasi. Jika hanya
runtime terpasang, instal **SDK**. SDK sudah mencakup runtime.

## 3 Build dan uji tanpa robot

```powershell
dotnet build .\RmsCollector.csproj -c Release
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --mock --once --dry-run
```

Hasil yang diharapkan `[DRY ...] POST ...`, `[RMS] Done`, exit code 0. Teks POST
pada dry-run hanya menampilkan payload. Tidak ada pengiriman HTTP atau koneksi robot.
Tidak perlu token. Data mock ditandai `simulated=true`.

Source menyertakan DLL .NET resmi 1.1.10 pada `sdk/dotnet`. Jangan mencampur
`YMConnect_CS.dll` dan `YMConnect.dll` dari rilis/paket berbeda. Build menyalin
pasangan yang sesuai secara otomatis.

## 4 Periksa DLL robot

Jika diperlukan, instal runtime ini: https://aka.ms/vc14/vc_redist.x64.exe

```powershell
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --check-sdk
```

Pesan sukses membuktikan pemuatan DLL pada proses Windows x64, bukan koneksi robot.
Jika gagal, cek kedua DLL di output, versi SDK yang sama dan runtime Visual C++ x64.
Tidak perlu memasang compiler C++ untuk menjalankan pemeriksaan ini.

## 5 Atur backend dan robot

Buka `config/collector.dojo.conf`:

```ini
api_base=http://127.0.0.1:8080
token_file=./collector.token
spool_dir=./spool
telemetry_ms=5000
operating_every_s=60
alarms_every_s=30
extras_every_s=10
http_timeout_s=5
spool_max=20000
robot=1,192.168.255.1,R1,10;10010
```

- `api_base` adalah alamat Laravel. `127.0.0.1` berarti komputer tempat collector
  berjalan. Jika Laravel di PC lain, gunakan IP PC tersebut dan portnya.
- Angka pertama di `robot=` adalah **ID robot di database**. Sesudahnya IP
  controller dan control group R1. ID harus terdaftar pada backend yang dipakai.
- `10;10010` adalah alamat pembacaan IO byte. Bisa dihapus bila tidak dibutuhkan:
  `robot=1,192.168.255.1,R1`. Collector tidak melakukan IO write.
- Path token dan spool relatif terhadap folder kerja terminal. Jalankan dari
  folder source atau folder aplikasi publish yang berisi config.
- Banyak robot memakai banyak baris `robot=` dengan ID dan IP unik. Contoh ada
  di `config/sample_config_8_robot`; batas pilot ini delapan robot.

Cek API tanpa token dan tanpa robot:

```powershell
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --probe
```

`GET /up` sukses belum membuktikan token, ID robot, tabel database atau POST berhasil.

Siapkan token collector dari lingkungan Laravel kamu:

```powershell
Copy-Item .\collector.token.example .\collector.token
notepad .\collector.token
```

Isi satu baris token asli, tanpa awalan `Bearer `. Token pribadi tidak disertakan
dalam ZIP. Jangan mengirim token ke chat atau commit ke GitHub. Jika menerapkan
patch ke repo lama yang sudah punya token, periksa token yang sesuai; tidak perlu
menimpa file tersebut dengan contoh.

## 6 Uji robot lalu jalankan pilot

Hubungkan PC dan controller sesuai jaringan DOJO yang sudah divalidasi.
YMConnect memakai Ethernet/UDP port 10040/10041. Migrasi bahasa tidak mengganti
parameter robot dan tidak menambahkan OPC UA.

```powershell
ping 192.168.255.1
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --real --once --dry-run
```

Perintah tersebut membaca satu siklus dan menampilkan data tanpa POST ke Laravel.
Bandingkan posisi, torsi, state dan waktu operasi dengan bukti DOJO. Program tidak
mengubah mode robot, servo power atau gerakan.

Setelah pembacaan sesuai, uji satu siklus dengan POST:

```powershell
dotnet run --project .\RmsCollector.csproj -c Release --no-build -- --config .\config\collector.dojo.conf --real --once
```

Periksa respons 2xx serta data backend/database. `[SPOOL ...]` berarti disimpan
lokal, bukan sudah tersimpan di SQL Server. HTTP 401/403/422 berarti perlu memeriksa
token, ID atau validasi. Payload baru yang ditolak disimpan ke
`spool/<id>/rejected`; penolakan permanen menghentikan program. Queue lama dengan
HTTP 422 dikarantina saat replay agar tidak mengulang payload invalid terus-menerus.

Hapus `--once` untuk pemantauan kontinu. Ctrl+C untuk berhenti. Tambahkan
`--robot-id 1` untuk memilih satu robot pada config. Hentikan collector C++ lama
sebelum menjalankan C# untuk robot/spool yang sama.

## 7 Bawa lewat HDD ke DOJO

Paket **Windows siap jalan** yang menyertai source sudah berisi .NET Runtime.
Ekstrak seluruh folder ke misalnya `C:\RMS-Collector`; jangan hanya menyalin EXE.
Cek config, buat token dan instal Visual C++ Redistributable x64 jika belum ada.

```powershell
Set-Location -LiteralPath 'C:\RMS-Collector'
.\rms_collector.exe --mock --once --dry-run --config .\config\collector.dojo.conf
.\rms_collector.exe --check-sdk
.\rms_collector.exe --config .\config\collector.dojo.conf --probe
.\rms_collector.exe --config .\config\collector.dojo.conf --real --once --dry-run
```

Untuk membuat ulang paket sendiri, jalankan pada laptop saat ada koneksi:

```powershell
dotnet publish .\RmsCollector.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

Salin seluruh `publish\win-x64` setelah selesai. Publish pertama mungkin perlu
mengunduh runtime pack Windows. Build standar menggunakan SDK dan DLL yang sudah
ada; proyek tidak memakai NuGet pihak ketiga. Native AOT, trimming dan single-file
tidak diaktifkan agar DLL vendor tetap bisa diperiksa. Update runtime paket
self-contained dilakukan lewat publish ulang dan penggantian folder aplikasi.

## 8 Batas verifikasi

Kompilasi, mock, kesetaraan payload C++, antrean dan publish Windows telah diperiksa
di lingkungan pengembangan. Native DLL Windows dan pembacaan robot fisik DOJO
belum dijalankan di lingkungan pengerjaan ini. Check-sdk dan uji real perlu
kamu jalankan pada Windows. Berhasil build tidak sama dengan berhasil membaca robot.

`value_seconds` dihitung dari elapsed Hours/Minutes/Seconds. Backend Laravel tetap
bisa mengosongkannya sampai aturan bukti pendant untuk PM diaktifkan. Migrasi ini
tidak mengubah kebijakan bukti atau schema database backend.

## Referensi resmi

- YMConnect C#: https://developer.motoman.com/en/YMConnect/CSharpWindows
- SDK: https://github.com/Yaskawa-Global/YMConnect/releases/tag/v1.1.10
- .NET: https://learn.microsoft.com/en-us/dotnet/core/install/windows
- Publish: https://learn.microsoft.com/en-us/dotnet/core/deploying/
- VC++ runtime: https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist
