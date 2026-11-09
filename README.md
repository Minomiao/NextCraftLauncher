# Next Craft Launcher

一个 Minecraft 启动器
仍在制作中...

## 功能

- 下载 Minecraft 版本（正式版 / 快照 / 远古版本）
- 安装 Fabric / Forge / NeoForge
- 扫描 `.minecraft` 中的游戏版本，可切换或添加游戏目录，也能导入 PCL2 的启动设置
- 支持 Microsoft 账户（正版）登录，也可使用离线账号
- 自动识别已安装的 Java，并按版本需要下载对应的 Java 运行时
- 从 Modrinth / CurseForge 搜索并下载单个 Mod，支持中文名检索
- 下载源可在自动 / 官方 / BMCLAPI 之间切换

## 使用

默认扫描 `%APPDATA%\.minecraft`，可在界面中修改路径。

配置与日志保存在启动器所在目录的 `NCL` 文件夹中：

- `NCL\settings.json`：游戏目录、内存、Java、下载源等设置
- `NCL\account.json`：已登录的账号（正版刷新令牌，请勿分享）
- `NCL\log\`：运行日志，按天分文件，保留 7 天
- `NCL\cache\`：可重建的缓存，包括版本清单、加载器版本列表、Forge 安装器等
  - `NCL\cache\mods\icons\<平台>\<工程 ID>-<链接哈希>.<扩展名>`：Mod 图标
  - `NCL\cache\mods\search\<平台>\<版本>_<加载器>_<关键词>-p<页码>.json`：Mod 搜索结果（48 小时内复用）
  - `NCL\cache\mods\versions\<平台>\<工程 ID>.json`：Mod 文件列表（48 小时内复用）
  - `NCL\cache\mods\aliases.json`：中文名查询结果
  - Mod 缓存（图标 / 搜索 / 文件列表）总量不超过 500 MB，超出后按最后写入时间清理最旧的文件

## 参考

开发过程中参考了 [PCL2](https://github.com/Meloong-Git/PCL)（Plain Craft Launcher 2）的
交互与实现思路，包括资源下载页的分区方式、下载源之间切换与超时的策略，
以及 Mod 搜索与图标的加载流程。

本项目为独立实现，不包含 PCL 的代码或数据文件。中文搜索优先使用自己的别名表
`src/NextCraftLauncher.Download/Mods/Data/ModAliases.txt`，未收录时查询
[MC百科](https://www.mcmod.cn/) 的搜索结果取得英文名，结果缓存在
`NCL\cache\mods\aliases.json`。MC百科内容以 BY-NC-SA 3.0 协议提供。
