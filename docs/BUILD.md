# 建置與封裝

在 Windows x64 使用 PowerShell 7.4 以上，從專案根目錄執行。首次準備需要網路，`bootstrap.ps1` 會依 `global.json` 準備固定版本的 .NET SDK 與媒體工具。

```powershell
./scripts/bootstrap.ps1
./scripts/make-fixtures.ps1
./scripts/build.ps1
./scripts/run.ps1 -Fixture -DataDirectory ./artifacts/manual-data
```

驗收資料存於 `artifacts/`，各驗收程式使用獨立資料目錄。測試靜音只作用於測試音訊串流。

```powershell
./scripts/verify-single-machine.ps1
./scripts/verify-scoring.ps1
./scripts/verify-phone.ps1
./scripts/verify-ui-contract.ps1
./.tools/dotnet/dotnet.exe run --project tests/OpenKaraoke.Creation.Tests -c Release -- .
./.tools/dotnet/dotnet.exe run --project tests/OpenKaraoke.Workflow.Tests -c Release -- .
./.tools/dotnet/dotnet.exe run --project tests/OpenKaraoke.SongScoring.Tests -c Release -- .
```

手機網頁驗收 `verify-phone-browser.ps1` 需要 Node.js 和 Edge。加速驗收 `OpenKaraoke.Acceleration.Tests` 可加 `--require-gpu` 要求實際 GPU 運算。實體手機聽感、硬體呈現率及端到端延遲應在目標裝置驗證。

版本號設於 `src/OpenKaraoke.Prototype/OpenKaraoke.Prototype.csproj` 的 `Version`。完成驗收後，先封裝並驗證執行版，再封裝同版原始碼。

```powershell
./scripts/build.ps1 -Publish
./scripts/verify-ui-contract.ps1 -Packaged
./scripts/verify-scoring.ps1 -Packaged -SkipCore
./scripts/verify-phone.ps1 -Packaged -SkipCore
./scripts/package-source.ps1
Get-FileHash ./dist/OpenKaraoke-Standalone-win-x64.zip, ./dist/OpenKaraoke-Standalone-source.zip -Algorithm SHA256
```

`-Publish` 產生 `dist/OpenKaraoke-Standalone/` 及 `dist/OpenKaraoke-Standalone-win-x64.zip`。可用 `-OutputDirectory` 指定專用的輸出目錄。封裝會拒絕覆寫含使用者資料或非套件檔案的目錄。啟動器從 `app/OpenKaraoke.dll` 載入程式及隨附的執行環境。除錯符號存於 `artifacts/symbols/`，該處的 `manifest.json` 記錄程式與符號的 SHA-256。

`dist/OpenKaraoke-Standalone-source.zip` 含程式、測試、工具、授權、合成影片及第三方對應來源。第三方來源依 `tools/manifest.json` 核對。原始碼 ZIP 排除 SDK、建置輸出、測試產物及個人資料。

驗收後，由儲存庫擁有者推送與專案版本一致的正式標籤，例如 `v1.0.1`。GitHub Actions 會從標籤建置，將兩個 ZIP 上傳至 Release 草稿，確認附件完整後發布，並在版本說明列出 SHA-256。一般分支推送不會發布 Release。若上傳失敗，請先檢查 Actions 紀錄及未發布的草稿。

原生元件的核對與重建方式見[第三方聲明](../THIRD-PARTY-NOTICES.md)。
