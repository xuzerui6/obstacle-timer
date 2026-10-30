# 障碍竞速 · 视频躯干主计时 / 0.5.0

0.5.0：修复同路连续过线、组次停录与保存确认、状态/来源更正、心跳操作干扰和补传调度；新增原片逐帧补检、暂停/续检与重载恢复。

Windows 计时台、Timy3 USB 接收与手机网页。多人共用 C0 起跑，手机接近终点时手动开启，BodyPix 只提取胸腹躯干前缘产生过线候选，裁判在电脑匹配号码。C1/C1M 进入独立光电门备份队列；绑定号码不会自动采用备份。

**本次交付是候选检测与人工备份版。未提供任何真实达标配置，视频自动确认关闭，不宣称达到 0.01 秒。** 完整配置须至少100次可追溯的独立端到端比较、最大绝对误差≤10ms，并通过当前同步、画面、证据和原片帧对应核验，才可自动确认视频成绩。Wi-Fi RTT 不能证明计时精度。

1. 安装 `artifacts/ObstacleTimer-Setup-0.5.0-win-x64.exe`，或运行 `artifacts/windows-0.5.0/ObstacleTimer.exe`，保留现有 Timy3 驱动。
2. Timy3 开机进入 STOPWATCH，关闭占用 USB 的 ExcelWriter 等程序。软件不修改设备固件或计时参数。
3. 同一 Wi-Fi，电脑「设备与配对」选择局域网地址，手机扫码并信任本机 HTTPS 证书。配对后仅预加载本地模型，相机关闭。
4. 勾选多人、确认精度/舍入、创建组次并待发令。一次 START 共用起跑；选手接近终点时手机设置终点线、方向、区域，点击「开启相机并录像」。
5. 电脑「视频候选与来源复核」匹配轨迹与号码；未达标视频保留候选。需要有效备份成绩时，现场页先匹配 C1，再在复核页填写原因并明确启用光电门备份。
6. 整组结束后才停录像，逐人选帧复核，先传截图与掩膜，后补传同组原片。未完成原片对应核验的结果保持待处理。

详见 [操作指南](docs/操作指南.md)、[精度验证与配置格式](docs/精度验证.md)、[验证记录](docs/验证记录.md)、[手机部署](docs/浏览器部署.md)及 [v4协议](docs/protocol.md)。

SQLite v1/v2/v3 升级前生成 `timing.pre-v4-时间.sqlite`，手机 IndexedDB 升级至 v3，保留旧原片和未完成队列。详情见 [迁移与回滚](docs/迁移说明-0.5.0.md)。电脑默认数据目录 `%LOCALAPPDATA%/ObstacleTimer`；成绩、判读与来源操作分别留历史。安装器不关闭正在运行的旧程序，程序文件升级前保留回滚副本。

原片补检：手机恢复已保存的本组录像，展开「重新检测原片」，设置片段后开始。它不打开相机，可暂停、重载后继续，新组录像自动暂停后台任务。补检或人工候选缺少有效采集时钟，用时留空，不会覆盖实时事件或成绩。

构建：`.\scripts\import-timy-sdk.ps1`、`.\scripts\build-windows.ps1`；测试：`.\.tools\dotnet\dotnet.exe run --project tests/ObstacleTimer.Tests -c Release`；源码打包：`python scripts/package-source.py`。需要 .NET 10 SDK、Node 22+、pnpm；最终安装包自带 .NET、网页、本地模型及WASM，无需 Node 或互联网。模型资源由 `scripts/verify-vision-assets.ps1` 校验固定SHA256；缺失时用 `scripts/fetch-vision-assets.py` 从官方源恢复。

架构：`Core` 保存状态和计算；`Desktop` 提供 WPF、SQLite、HTTPS/WSS 与审核；`TimyBridge` 为独立 .NET Framework 4.8 x64 进程；`web/phone` 为 React/TypeScript、BodyPix Worker、录像与恢复。`mobile/android`、`mobile/ios` 是早期v1参考，不作为本版交付。测试模拟需显式 `--simulate`，不连接真机、不进入正式排名；本机测试控制仅限 `--headless --simulate` 的 stdin。

软件回归可通过 `.github/workflows/regression.yml` 在 Windows 重复运行；包括原有158项电脑端/12项网页检查以及新增回归、真实 BodyPix 原片补检和 WPF 操作自动化。实测结果见 [0.5.0缺陷验证](docs/缺陷验证-0.5.0.md)。

当前尚缺真实苹果/安卓机型与独立参考测量、标注视频集、现场多人验收。不能以模拟测试、模型成功加载或光电门被手臂提前遮挡的时刻替代躯干过线真值。
