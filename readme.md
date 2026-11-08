# DOTNET untuk yaskawa collector data
## Langkah-Langkah dari awal hingga jalan program nya 
### cara cek list aakaha sdk .net sudah terinstall apa belum
```
dotnet --list-sdks
```
### Build kode menjadi aplikasi
```
dotnet build .\RmsCollector.csproj -c Release
```
### cara running hasil build 
```
.\bin\Release\net10.0\rms_collector.exe --config .\config\collector.dojo.conf --real
```

