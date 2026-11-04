# 第三方元件

OpenKaraoke 自有程式碼、文件與自製素材採用 `GPL-3.0-only`，全文見 [LICENSE](LICENSE)。第三方內容保留其原有授權、著作權與適用條件，不因本專案採用 GPL 而改變。

## 隨執行套件提供

- .NET／WPF 10.0.14：Microsoft 與 .NET contributors。一般檔案採 [MIT](licenses/dotnet-MIT.txt)，部分原生元件適用 [.NET Library 條款](licenses/dotnet-LICENSE.txt)。元件區分見 Microsoft 的 [Windows 授權說明](licenses/dotnet-Windows-License-Information.md)，另保留 [第三方聲明](licenses/dotnet-ThirdPartyNotices.txt)。[WPF 來源](https://github.com/dotnet/wpf/tree/v10.0.14)、[Runtime 來源](https://github.com/dotnet/runtime/tree/v10.0.14)
- mpv `0.41.0-10`：mpv／MPlayer／mplayer2 contributors，使用 MSYS2 UCRT64 官方建置。主專案為 GPL-2.0-or-later；保留 [GPL](licenses/mpv-LICENSE.GPL)、[LGPL](licenses/mpv-LICENSE.LGPL) 全文及來源中的個別聲明，連入的 FFmpeg 等元件依其條款。執行檔、DLL、二進位套件、對應來源及 SHA256 見 `tools/manifest.json`。[套件來源](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-mpv)
- FFmpeg／FFprobe `9.0.2-2`：使用 MSYS2 UCRT64 官方 GPLv3 或後續版本建置；全文見 [ffmpeg-GPLv3.txt](licenses/ffmpeg-GPLv3.txt)。用於影片、音軌、媒體分析、YouTube 下載後整合及演唱錄音合成。兩者與 mpv 及所需 DLL 共用 `tools/media/` 目錄，不複製單一執行檔到 AI 環境。[套件來源](https://packages.msys2.org/packages/mingw-w64-ucrt-x86_64-ffmpeg)
- mpv／FFmpeg 原生相依元件：各套件完整授權、著作權及例外條款保留於 `licenses/msys2/`，不能以本專案 GPL 統一取代。清單、個別授權檔雜湊及來源版本見 `tools/manifest.json`；原始建置資訊見 [Build-Info.json](licenses/msys2/Build-Info.json)。Rust 元件另保留 [相依套件聲明](licenses/msys2/Rust-ThirdPartyNotices.txt)。
- Microsoft D3DCompiler：Microsoft Corporation。WPF 隨附的 `D3DCompiler_47_cor3.dll` 依 Microsoft 指定的 [Windows SDK 條款](licenses/Windows-SDK-License.md) 提供；另保存官方 [REDIST 清單](licenses/Windows-SDK-REDIST.md) 及 [.NET 元件授權區分](licenses/dotnet-Windows-License-Information.md)。本程式為傳統 Windows 桌面程式；散布及使用相應 Microsoft 元件須遵守其條款，包括著作權保留、平台限制及要求散布者與外部使用者同意保護該元件的條款，不將其改授權為 GPL。
- Microsoft.Data.Sqlite 10.0.14：MIT，著作權 .NET Foundation and Contributors。全文見 [Microsoft.Data.Sqlite-MIT.txt](licenses/Microsoft.Data.Sqlite-MIT.txt)。[來源](https://github.com/dotnet/efcore)
- SQLitePCLRaw 2.1.13：Apache-2.0，Copyright 2014-2024 SourceGear, LLC。全文見 [SQLitePCLRaw-Apache-2.0.txt](licenses/SQLitePCLRaw-Apache-2.0.txt)。[來源](https://github.com/ericsink/SQLitePCL.raw)。隨附 SQLite 3.53.3，SQLite 為[公有領域](https://sqlite.org/copyright.html)。
- NAudio.Core／NAudio.Wasapi 2.2.1：MIT，用於 WASAPI 麥克風收音與驗證器。全文見 [NAudio-MIT.txt](licenses/NAudio-MIT.txt)。[來源](https://github.com/naudio/NAudio/tree/v2.2.1)
- SwiftF0 0.3.0：Lars Nieradzik，MIT。使用上游原始 ONNX 模型，來源與 SHA256 固定於 `tools/scoring/manifest.json`；全文見 [SwiftF0.txt](licenses/SwiftF0.txt)。C# 串流保留上游前後幀規則，音符分段移植自上游的 [`segment_notes`](https://github.com/lars76/swift-f0/blob/v0.3.0/swift_f0/music.py)。[來源](https://github.com/lars76/swift-f0/tree/v0.3.0)
- Microsoft.ML.OnnxRuntime.Gpu.Windows 1.23.2：Microsoft，MIT，提供 CPU／CUDA 推論。授權與第三方聲明見 [ONNX-Runtime-MIT.txt](licenses/ONNX-Runtime-MIT.txt)、[ONNX-Runtime-ThirdPartyNotices.txt](licenses/ONNX-Runtime-ThirdPartyNotices.txt)。CUDA／cuDNN DLL 沿用本機 AI 的官方 PyTorch 安裝，不包含於主程式 ZIP。[來源](https://github.com/microsoft/onnxruntime/tree/v1.23.2)
- Lucide 1.17.0：使用官方圖示節點輸出的 SVG；ISC／部分 Feather 圖示 MIT，全文見 [Lucide-ISC.txt](licenses/Lucide-ISC.txt)。素材清單與雜湊見 `src/OpenKaraoke.Prototype/Assets/Icons/manifest.json`；`docs/assets/logo.svg` 使用其中的 audio-lines 圖示並調整描邊顏色。[來源](https://unpkg.com/lucide@1.17.0/dist/umd/lucide.js)
- ASP.NET Core／Kestrel 10.0.14：MIT，用於本機手機麥克風 HTTPS／WebSocket 服務。授權與第三方聲明見 [ASP.NET-Core-MIT.txt](licenses/ASP.NET-Core-MIT.txt)、[ASP.NET-Core-ThirdPartyNotices.txt](licenses/ASP.NET-Core-ThirdPartyNotices.txt)。[來源](https://github.com/dotnet/aspnetcore)
- QRCoder 1.8.0：MIT，用於本機產生配對 QR Code；全文見 [QRCoder-MIT.txt](licenses/QRCoder-MIT.txt)。[來源](https://www.nuget.org/packages/QRCoder/1.8.0)

## 首次使用時下載

以下大型執行元件不隨主程式或原始碼 ZIP 提供。下載來源與 SHA256 固定於 `tools/creation/runtime-manifest.json`，Python 套件版本固定於 `tools/creation/requirements.txt`。下載後的原始授權與聲明仍須保留。

- Demucs 4.0.1／HTDemucs：Meta Platforms, Inc.，MIT。全文見 [Demucs LICENSE](licenses/ai/demucs/licenses/LICENSE)。模型僅從官方固定網址下載並驗證完整 SHA256，不接受使用者提供的 Python checkpoint。[來源](https://github.com/facebookresearch/demucs)
- PyTorch／TorchAudio 2.8.0：PyTorch contributors，BSD 類授權；相關 NVIDIA 元件依各自授權。全文與聲明見 [torch LICENSE](licenses/ai/torch/LICENSE)、[torch NOTICE](licenses/ai/torch/NOTICE)、[TorchAudio LICENSE](licenses/ai/torchaudio/licenses/LICENSE)。使用官方 Windows CPU／CUDA 12.8 wheel；目前保存的授權檔取自 `2.8.0+cu128`，CPU wheel 的隨附文件須依實際下載內容保留。[官方版本來源](https://pytorch.org/get-started/previous-versions/)
- Python 3.12.16：Python Software Foundation License，使用 Astral python-build-standalone `20261026` 發布版本。全文見 [Python LICENSE.txt](licenses/ai/python/LICENSE.txt)，執行環境另有的相依元件聲明仍須保留。[來源](https://github.com/astral-sh/python-build-standalone)
- 其他固定版本的 Python 套件：各自的授權全文、版本、來源及檔案 SHA256 見 [AI 授權清單](licenses/ai/manifest.json)；包含 lameenc 的 LGPL、tqdm 的 MPL／MIT、retrying 的 Apache 授權與 NOTICE 等，不統一改為 GPL。
- yt-dlp 2026.10.26：主要程式 Unlicense；官方獨立執行檔另含相依元件。保留 [Unlicense](licenses/yt-dlp-Unlicense.txt) 與該版本完整的 [第三方聲明](licenses/yt-dlp-ThirdPartyNotices.txt)。[來源](https://github.com/yt-dlp/yt-dlp/tree/2026.10.26)
- Deno 2.9.8：主程式 MIT，全文見 [Deno-MIT.txt](licenses/Deno-MIT.txt)；編入執行檔的相依元件依各自條款，不能只以此 MIT 全文代表整個執行檔。[來源](https://github.com/denoland/deno/tree/v2.9.8)

## 僅供開發與驗收

- 7-Zip reduced console 26.04：僅供開發工具解壓縮，不隨執行或原始碼 ZIP 提供執行檔。LGPL／BSD 及 unRAR 限制等適用範圍以 [7-Zip-License.txt](licenses/7-Zip-License.txt) 為準。[來源](https://github.com/ip7z/7zip/tree/26.04)
- playwright-core 1.56.1：只供瀏覽器驗收，不隨正式程式提供。驗收腳本下載官方 npm 套件並檢查 SHA512；保留 [Apache-2.0](licenses/Playwright-Apache-2.0.txt)、[NOTICE](licenses/Playwright-NOTICE.txt) 與 [第三方聲明](licenses/Playwright-ThirdPartyNotices.txt)。[來源](https://github.com/microsoft/playwright/tree/v1.56.1)

## 原始碼與散布條件

`OpenKaraoke-Standalone-source.zip` 提供本專案的完整程式、測試、重建工具與授權文件，並在 `third-party/sources/` 附上 `tools/manifest.json` 所列原生套件的對應來源，包括上游原始碼、建置腳本、修補及 Rust registry 相依套件。來源套件的版本與實際散布 DLL 的所屬二進位套件逐項對應；建置資訊及來源清單保留完整 SHA256。建置與封裝步驟保留於原始碼的 [build.ps1](https://github.com/tm3u/OpenKaraoke/blob/main/scripts/build.ps1) 與 [package-source.ps1](https://github.com/tm3u/OpenKaraoke/blob/main/scripts/package-source.ps1) 腳本說明，可在原始碼根目錄以 `Get-Help ./scripts/build.ps1 -Full` 及 `Get-Help ./scripts/package-source.ps1 -Full` 查看。

散布執行套件時須一併提供相同版本的原始碼套件與授權文件，保留收件者依各元件授權享有的權利。首次下載的 Python／AI 環境未附在執行 ZIP；若另行重包，須另外確認其完整相依、授權及對應原始碼義務。

本檔與來源連結不取代各元件的授權全文、適用條件或完整對應原始碼提供義務；軟體授權亦不授予歌曲、MV 或使用者錄音的利用權利。

## 原生元件重建

mpv、FFmpeg 與 FFprobe 使用 MSYS2 UCRT64 官方建置，版本、每個執行檔／DLL、來源套件與 SHA256 固定於 `tools/manifest.json`。`scripts/prepare-media.ps1 -VerifyOnly` 核對已安裝的工具；`-SourcesOnly` 取得並核對對應來源。缺少來源或雜湊不符時，封裝不會完成。原始碼套件將來源放於 `third-party/sources/`，解壓後可直接驗證，不必重新下載已附的來源。

重建原生元件需使用 MSYS2 UCRT64，依 [Build-Info.json](licenses/msys2/Build-Info.json) 準備對應工具鏈與建置依賴，解開各 `.src.tar.zst` 後，在其 `PKGBUILD` 所在目錄執行 `MINGW_ARCH=ucrt64 makepkg-mingw -s`。來源套件包含上游原始碼及修補；Rust 元件另附 `Cargo.lock` 所列 crates 與建置腳本指定的 Windows 相依替換版本。不同工具鏈與環境不保證產生位元完全相同的執行檔。

AMF 僅使用標頭，另附純標頭來源套件及原始建置腳本；所有標頭已逐檔確認與上游版本相同，不附完整 SDK 的執行檔與示範媒體。該套件不需編譯，可在 MSYS2 使用 `pacman -U <標頭套件.pkg.tar.zst>` 安裝。

## 專案授權與合法使用

除另有標示的第三方內容外，程式碼、文件與自製素材採 **GPL-3.0-only**（GNU General Public License 第 3 版，僅限該版本）。允許使用、修改、商用與販售；散布程式或改版時，須依 GPL 提供完整對應原始碼，保留授權與著作權聲明，並依該授權散布受其規範的改作。程式不提供保固，完整條件見 [LICENSE](LICENSE)。

第三方元件、模型與圖示依各自授權，見本檔與 [licenses/](licenses/)。軟體授權不授予歌曲、MV 或使用者錄音的利用權利，也不取代影音平台的使用條款。

OpenKaraoke 提供技術研究、程式交流與合法影音處理功能，不附帶歌曲、MV 或商業歌曲庫。請僅處理自有著作、已取得所需授權，或依法允許利用的內容；下載、人聲分離、錄音、公開播放及再散布，均須遵守著作權法與來源平台條款。

保留 YouTube 功能不代表已取得平台或權利人的許可。「研究」、「個人使用」或「非營利」不會自動構成合理使用，也不是免責保證。請確認 [YouTube 服務條款](https://www.youtube.com/t/terms)，台灣利用情境可參考[智慧財產局說明](https://www.tipo.gov.tw/tw/copyright/692-16450.html)。上述聲明不對 GPL 軟體增列「僅限研究」或「禁止商用」限制。
