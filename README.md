# Next Craft Launcher

一个参考 PCL2 体验、基于 Avalonia 的 Minecraft 启动器。

当前阶段：可以扫描 `.minecraft` 中的版本、解析版本 JSON、自动寻找 Java、从 PCL2 导入设置，在文件完整的前提下启动游戏；同时内置独立下载核心，支持下载 Mojang 全量游戏版本与 Java 运行时，并可切换官方源 / BMCLAPI。

## 技术栈

- .NET 10（`net10.0`）
- Avalonia 12.1.4（Fluent 主题）
- CommunityToolkit.Mvvm 8.4.2（`[ObservableProperty]` / `[RelayCommand]`）
- 独立的 `NextCraftLauncher.Download` 下载核心（HTTP 重试 / 断点续传 / SHA-1 / 镜像）

## 目录结构

```
Next Craft Launcher/
├─ NextCraftLauncher.sln
├─ global.json                          # 锁定 .NET 10 SDK
├─ src/
│  ├─ NextCraftLauncher.Core/           # 与 UI 无关的核心逻辑
│  │  ├─ Minecraft/                     # 版本 JSON、继承链合并、rules、扫描
│  │  ├─ Java/                          # Java 发现与版本识别
│  │  ├─ Launch/                        # 参数构建、natives 解压、进程启动、离线 UUID
│  │  └─ Settings/                      # settings.json 与 PCL2 导入
│  ├─ NextCraftLauncher.Download/       # 下载核心
│  │  ├─ Http/                          # 通用下载器（重试/续传/校验/限流回退）
│  │  ├─ MirrorSpeedProbe.cs            # 「自动」下载源的测速选择
│  │  ├─ Java/                          # Mojang Java 运行时下载与目录
│  │  └─ Minecraft/                     # Mojang 版本清单与版本下载
│  └─ NextCraftLauncher/                # Avalonia 应用
│     ├─ Properties/PublishProfiles/    # win-x64 单文件发布配置
│     ├─ ViewModels/
│     └─ Views/
│        ├─ MainWindow.axaml            # 沉浸式标题栏 + 工具栏 + 状态栏/日志
│        ├─ LaunchView.axaml            # 启动页
│        └─ DownloadView.axaml          # 下载页
└─ README.md
```

## 已实现

- 扫描 `<.minecraft>/versions/*/*.json`，按类型与自然版本号排序
- `inheritsFrom` 继承链合并：主类、资源索引、依赖库（按 `group:artifact:classifier` 去重）、启动参数
- `rules` 求值：操作系统、架构、系统版本区间、features（Demo、自定义分辨率等默认关闭）
- 同时支持新的 `arguments` 格式（字符串与 `{rules, value}` 混排）和旧的 `minecraftArguments`
- 支持新版 JSON 的 `default-user-jvm`，但剔除 `-Xms/-Xmx`，内存由界面设置控制
- 版本隔离：按 PCL2 的实际规则判断。版本级 `VersionArgumentIndieV2` / 旧版 `VersionArgumentIndie`（1 开 / 2 关）→ 版本目录内 `mods` 有文件或 `saves` 有子目录 → 全局 `LaunchArgumentIndieV2` 的 0–4 模式（0 关，1 仅隔离可装 Mod 的版本，2 仅隔离非正式版，3 两者，4 全部）；也兼容 HMCL 的 `hmclversion.cfg`。界面可手动覆盖，启动时据此决定 `--gameDir`
- PCL2 配置导入：点击「导入 PCL2」后可选择 **PCL 启动器 exe**、**PCL 文件夹**或**游戏目录**。会向上查找 PCL2 基目录（`PCL.exe` / `Plain Craft Launcher 2.exe` 所在目录），读取 `<基目录>/PCL/Setup.ini`，并根据 `LaunchFolderSelect`（`$` 表示基目录）定位实际游戏目录；之后可导入内存、版本隔离、指定 Java / Java 版本范围，并自动选择 `LaunchVersionSelect` 记录的版本
- 旧版 natives classifier 解压到 `versions/<id>/natives`；带 `natives` 映射的库不会把基础 jar 放进 classpath（与 PCL2 行为一致）；新版的 natives 以普通依赖形式进入 classpath，由 LWJGL 自行解压
- Java 自动发现：`JAVA_HOME`、当前游戏目录与默认 `%APPDATA%\.minecraft\runtime`、常见 JDK 安装目录、`PATH`，按版本要求挑选最合适的版本
- 离线启动：生成与 Java `UUID.nameUUIDFromBytes` 一致的离线 UUID
- 启动前检测缺失文件与 Java 版本，给出明确提示
- 启动后实时显示游戏的 stdout/stderr 和退出代码
- 沉浸式界面：`WindowDecorations="BorderOnly"` + 扩展客户区，无系统标题文字。左上角为「NEXT」字样，导航按钮（启动 / 下载）居中，右上角为自绘的最小化 / 最大化 / 关闭按钮；拖动标题栏空白区域可移动窗口，双击可最大化或还原
- 布局分区：`.minecraft` 路径、重新扫描与 PCL2 导入集中在工具栏；运行状态与「运行日志」开关收纳在底部状态栏，日志面板默认收起，启动游戏时自动展开
- 日志：启动器运行日志、游戏 stdout/stderr、加载器安装器输出写入 `NCL/log/launcher-yyyy-MM-dd.log`（按天分文件），启动时自动清理 7 天前的日志；未处理异常与未观察的任务异常也会记录，日志写入失败不影响主流程
- 视觉样式统一：卡片、工具栏、分组标题与说明文字使用 `App.axaml` 中的全局样式；下载页去掉重复的大标题，Tab 标题与正文层级统一。同时修复了「全部 Java 运行时」列表一直为空的问题（此前界面绑定的集合未被填充）
- 下载页「游戏版本」子页：从 Mojang `version_manifest_v2.json` 加载全部版本（当前 920 个，含正式版、快照、远古版本），支持版本号搜索、类型筛选、显示已安装标记，并标注最新正式版/快照；首次进入下载页时自动加载，也可手动刷新
- 版本详情：点击版本列表中的条目进入详情页，按 Forge / Fabric / NeoForge 分组展示该 Minecraft 版本可用的加载器版本（推荐/最新/稳定标记）。分组可随时展开，无可用版本或加载失败时展开显示占位说明，数据到达后再原位填回。加载器版本行**单选**（再次点击取消），底部只保留一个下载按钮：未选加载器时为「下载选中版本」，选中后为「下载并安装 …」，会先补齐原版再安装加载器。底部可自定义版本名，默认「版本-加载器 版本」（如 `1.21.1-Fabric 0.20.1`），选中加载器后自动填入，点「默认」可恢复；改名会同步更新版本目录、版本 JSON 的 `id` 与客户端 jar 名
- 加载器安装：Fabric 走官方 profile JSON（`versions/fabric-loader-<loader>-<mc>/`），按 profile 中的 maven 坐标补齐库文件；Forge / NeoForge 下载官方 installer 到 `NCL/cache/installers/`，调用 `--installClient` 完成安装（自动补充空的 `launcher_profiles.json`，安装器自身返回 0 但输出错误时也会按失败处理）。Forge / NeoForge 需要可用的 Java（按原版 JSON 的 `javaVersion` 要求选择），Fabric 不需要 Java。安装完成后会自动刷新版本列表并选中新版本
- 速度优化：启动后后台预加载 Forge / NeoForge 索引，详情页首次打开不再付索引下载时间；后台预取在版本清单加载、游戏下载或安装期间自动暂停，把带宽让给前台任务；原版下载与加载器安装合并为一条 0-100 进度（原版占前 65%，加载器占剩余部分，不重置），Forge / NeoForge 安装器阶段用平滑计时推进，并实时显示安装器输出
- 加载器版本索引：Forge 与 NeoForge 优先使用 Prism Launcher 元数据索引（`meta.prismlauncher.org`，GitHub Pages，一次返回全部版本，原始索引缓存在 `NCL/cache/indexes/forge.json` / `neoforge.json`，磁盘 6 小时），并直接利用其中的 recommended 标记；索引不可用时回退官方 maven（「自动」模式经 BMCLAPI 代理）。Fabric 仍使用官方 `meta.fabricmc.net/v2/versions/loader/<mc>`，因为 Prism 的 Fabric Loader 条目只声明依赖 intermediary、不包含 MC 版本要求，无法按游戏版本过滤。已适配新的年份式版本号（如 MC 26.3 → NeoForge `26.3.0.x`）
- 后台预取核心：启动 2 秒后在后台按「正式版优先、新版本优先」逐版本获取 Forge / Fabric / NeoForge 加载器列表，版本之间随机停顿 350–900 ms，有效缓存直接跳过，重启后从上次进度继续；每轮扫描结束后休眠 1 小时再检查。结果按「加载器 / 游戏版本」存放在 `NCL/cache/loaders/<loader>/<minecraft>.json`，留存与有效期均为 72 小时，过期文件在下一轮开始时清理。打开详情页时先用缓存立即展示，同时仍会访问网络刷新，内容无变化时不重建行、不会跳动；网络失败时回退 72 小时内的缓存，不会把已有数据变成「获取失败」
- 游戏版本下载：选择版本后可下载版本 JSON、客户端 jar、依赖库、natives、资源索引与 assets objects。「开启版本隔离」默认勾选，下载完成后写入 PCL2 兼容的 `versions/<id>/PCL/Setup.ini`（`VersionArgumentIndieV2`），启动页会自动识别。支持并发下载、断点续传、SHA-1 校验和已存在文件跳过；`versions/<id>/<id>.json` 与 `assets/indexes/<id>.json` 校验通过时直接复用，补文件或重装不再重复下载元数据
- 下载源：统一在下载页「下载设置」中选择，不再在路径栏单独放全局下拉框。游戏版本源同时用于版本清单、游戏文件与 Java 下载；Forge / Fabric / NeoForge 源用于各自版本列表。映射规则：`piston-meta` / `piston-data` / `launchermeta` → `bmclapi2.bangbang93.com`，`libraries.minecraft.net` → `bmclapi2.bangbang93.com/maven`，`resources.download.minecraft.net` → `bmclapi2.bangbang93.com/assets`
- 下载设置：下载页新增「下载设置」子页，可分别为游戏版本、Forge、Fabric、NeoForge 选择源（默认「自动」），分别保存为 `settings.json` 的 `GameMirror` / `ForgeMirror` / `FabricMirror` / `NeoForgeMirror`。游戏版本提供自动 / Mojang 官方 / Mojang CDN / BMCLAPI，加载器只提供自动 / 官方 / BMCLAPI（Mojang 源与加载器无关）。「下载后开启版本隔离」也移到此处，并保存为 `DownloadVersionIsolated`；切换源后回到对应子页时会重新获取列表
- 「自动」采用对冲竞速：官方主机立即请求，BMCLAPI 延后 800 ms 加入，谁先完整读完响应体用谁。只比较响应头会让 BMCLAPI 的 302 节点假胜出（拿到重定向就卡住），因此以完整返回为准；所有请求共用同一个 `DownloadClient`，复用连接、DNS 与源健康度记忆
- 「Mojang CDN」只调整 `launchermeta` / `piston-meta` 两个元数据主机的先后顺序，对 libraries 和 assets 等同于官方，且不含 BMCLAPI 回退
- 下载加速措施：
  - 同一文件的所有候选源轮转尝试（原先是同一源重试 3 次才换源），镜像故障时立即接管
  - 停滞超时：连接或连续 20 秒收不到数据就放弃当前地址，避免死连接卡住整个下载；清单类元数据使用 8 秒的短超时
  - 限流/故障记忆：收到 429、503、502、504 的源在本次下载内直接跳过（实测 BMCLAPI 并发请求会大量返回 429），后续文件全部走官方，不再逐个重试
  - 并发提升：游戏版本按大小分池——大文件 16 个并行、assets 小文件 48 个并行（小文件受请求延迟而非带宽限制）；Java 运行时 8 个；启用 HTTP/2 与连接池复用（服务器不支持时自动回退 HTTP/1.1）
  - 进度按“下载成本”计算：每个文件在字节数之外额外计入 64 KB 的固定请求开销。1.21.4 的 assets 共 4,039 个文件，最后 3,060 个小文件只占 7% 字节，纯按字节会让进度条长时间停在 93%；按成本计算后这段约占进度条的 33%，配合小文件高并发推进更均匀
  - 进度文本实时显示速度（MB/s）
- 下载页「Java 运行时」子页：包含两部分。① 当前选中版本所需的 Java：按 `javaVersion.component` 自动下载到 `.minecraft/runtime/<组件>/`；② 全部 Java 运行时：列出 Mojang 为当前平台提供的全部组件（当前为 Java 25 / 21 / 17×3 / 16 / 8），每一项都可单独下载，并显示已安装标记。均支持并发下载、SHA-1 校验、断点续传、跳过已存在文件、实时进度条与百分比；下载完成后自动加入 Java 列表并选中

## 已知限制

- 版本下载与 Java 下载是两个独立步骤；新版 Minecraft 需要先在 Java 子页下载对应的 Java 运行时。
- BMCLAPI 对同一 IP 的并发请求限制很严（实测 10 次/分钟级别，超出返回 429），因此大批量 assets 下载会快速回退官方源；如网络环境对官方源不友好，仍可能较慢。
- PCL2 的账号 / 离线用户名保存在注册表中并加密（DES + 设备识别码），当前不导入；微软账号也不导入。
- PCL2 的自定义 JVM / 游戏参数（`LaunchAdvanceJvm`、`VersionAdvanceJvm` 等）暂不导入。
- PCL2 设为「自动分配内存」时不导入内存值，保留启动器当前设置。
- 仅支持离线登录，未接入微软账号。
- 未实现 pre-1.7 的 `assets/virtual/legacy` 资源虚拟化。
- 未处理版本 JSON 中的 `logging` 配置（log4j 配置文件下发）。
- 主要在 Windows 上验证；代码中保留了 macOS/Linux 的规则求值分支。

## 构建与运行

```bash
dotnet build NextCraftLauncher.sln
dotnet run --project src/NextCraftLauncher
```

默认扫描 `%APPDATA%\.minecraft`，可在界面顶部修改路径。设置保存在**运行中的 exe 同级** `NCL\settings.json`（开发时是 `src/NextCraftLauncher/bin/Debug/net10.0/NCL/settings.json`）；不同构建输出目录各有自己的一份配置，发布后就是 `<exe目录>\NCL\settings.json`。

Debug 构建会自动限定为当前开发机的 RID（如 `win-x64`）并跳过原生库的 `.pdb`，输出体积从约 566 MB 降到约 32 MB；输出路径仍是 `bin/Debug/net10.0`，`NCL` 配置目录不受影响。裁剪/压缩属于发布期能力，Debug 不启用。

## 发布单文件

```bash
# 输出：src/NextCraftLauncher/bin/Release/net10.0/win-x64/publish/NextCraftLauncher.exe
dotnet publish src/NextCraftLauncher -p:PublishProfile=win-x64
```

`win-x64.pubxml` 已配置为：自包含、单文件、压缩、包含原生库、`PublishTrimmed` + `TrimMode=partial`、`InvariantGlobalization`、禁用 ReadyToRun。发布后会清理 `.pdb`，输出目录只剩一个 exe（**约 23.3 MB**，裁剪前约 49.8 MB）。

裁剪能安全工作的两个前提已写入项目：

- `TrimmerRootAssembly` 保留 `NextCraftLauncher` / `NextCraftLauncher.Core` / `NextCraftLauncher.Download` 三个程序集，供 `ViewLocator` 反射创建视图；
- `JsonSerializerIsReflectionEnabledByDefault=true` 让 `System.Text.Json` 的反射序列化在裁剪后仍可用（配置、版本清单、Java 清单）；否则会报“Reflection-based serialization has been disabled”。

`SatelliteResourceLanguages=en` 会去掉其他语言的框架资源（应用自身的界面文案不受影响）；如果希望 .NET 异常消息也保持中文，改成 `zh-Hans;en`，体积会略增。

## 验证情况

- `dotnet build NextCraftLauncher.sln` 通过，无警告（.NET 10.0.402）
- 位置识别：分别传入 `D:\PCL`、`D:\Plain Craft Launcher 2.exe`、`D:\.minecraft`，均正确解析为基目录 `D:\`、配置 `D:\PCL\Setup.ini`、游戏目录 `D:\.minecraft`、首选版本 `1.8.9-Forge_11.15.1.1902-OptiFine_M5`
- 真实 `D:\.minecraft\1.8.9-Forge_11.15.1.1902-OptiFine_M5`：旧版参数格式，48 个依赖库、4 个 natives、0 缺失文件，自动选中 Java 8，隔离开启，生成 28 个启动参数且无残留 `${...}`，工作目录为版本目录
- 单文件发布：输出仅 `NextCraftLauncher.exe`（**23,318,170 字节**，裁剪前 49,777,206 字节），发布版启动冒烟测试通过，配置与版本清单缓存均能正常写入
- 版本下载本地端到端：版本 JSON、客户端、库、natives、资源索引、2 个 assets objects 均正确；OS rules 过滤生效；隔离开启/关闭均可写入；重复下载跳过已有文件；`VersionResolver` 解析 0 缺失
- 真实 1.8.9 下载（不含 assets objects）：38 个文件、约 29 MB；解析结果 30 个库、4 个 natives、0 缺失、Java 8；隔离配置写入成功
- Java 运行时目录：`windows-x64` 下解析出 7 个可用组件（25.0.1 / 21.0.7 / 17.0.15 ×3 / 16.0.1 / 8u51），并正确排除非运行时的 `minecraft-java-exe`
- 远程版本清单：成功解析 920 个版本，最新正式版 `26.3`、最新快照 `26.4-snapshot-5`；`26.3`、`26.2`、`1.8.9` 均存在，类型统计为快照 756、正式版 103、远古 61
- 顶部导航：启动页与下载页拆分为独立视图，导航按钮切换正常，构建与启动冒烟测试通过
- 下载核心本地端到端测试（临时 HTTP 服务器）：清单解析、目录创建、3 个文件下载、SHA-1 校验、断点续传（Range）、重复下载全部跳过均通过
- 真实 Mojang 清单：`java-runtime-epsilon`（25.0.1）共 493 项（411 个文件）；样例文件下载与哈希校验通过
- 完整 Java 25 下载：411 个文件、约 100.2 MB、94.8 秒，下载后 `java -version` 返回 0 并输出 `openjdk version "25.0.1"`；`JavaLocator` 识别为「Java 25 · 官方运行时」
- 对 `%APPDATA%\.minecraft`：识别到 `26.2`、`26.3-snapshot-2`，解析出 Java 25 需求和 88 个依赖库，并正确报告 81 个缺失文件；两个版本均未检测到隔离配置
- 隔离检测分支测试：PCL2 版本级 V2 开/关、旧版 `VersionArgumentIndie=2`、`mods` 有文件、HMCL、全局模式 0/1/4、模式 1 下可装 Mod/不可装 Mod 的区分、无 PCL 配置均符合预期
- PCL2 导入测试：版本级配置导入内存 8192 MB（滑块索引 25）、版本隔离开启、指定 Java 路径；全局导入内存 3072 MB（索引 15）；索引 12/15/25/33/34 → 1536/3072/8192/16384/18432 MB，与 PCL2 公式一致
- 用临时构造的最小版本做了端到端启动测试：真实拉起 Java 21 测试主类，确认 classpath、rules 过滤、占位符替换、`default-user-jvm` 处理、natives 解压、进程输出全部正常（测试脚手架已删除）
- 下载源解析：三种模式下的 URL 顺序均符合预期（官方 / 自动 / BMCLAPI），非 Mojang 域名的地址不会被改写；三种模式实际拉取 `version_manifest_v2.json` 与 Java 运行时清单均成功（920 个版本、7 个运行时）
- 下载优化（本地假服务器 + 真实网络）：
  - 候选源为「限流源 → 正常源」时下载成功，后续 8 个文件对限流源的请求次数仅为 1 次（原实现会每次重试）
  - 停滞源（返回响应头后不再发数据）在 20.0 秒后自动切换到正常源并校验通过
  - `DownloadBytesAsync` 同样支持限流回退与哈希校验
  - 自动测速：实测官方 0.35–0.47 MB/s、BMCLAPI 0.00–0.15 MB/s，自动选择官方；结果缓存生效，显式选择则原样返回
  - 真实 assets 文件（镜像优先）1.93 秒下载并 SHA-1 校验通过
  - 实测 BMCLAPI 并发 40 个请求中 32 个返回 429（`Retry-After-short: 54`），因此限流快速回退是必要的
- GUI 启动冒烟测试通过（Debug 与单文件发布版，含启动页/下载页）
