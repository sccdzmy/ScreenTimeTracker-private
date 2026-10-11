# 原生前端与替换边界

Windows 正式构建使用 .NET 10 WPF 界面。后台保留原入口 `ScreenTimeTracker.exe`，界面为独立的 `ScreenTimeTracker.Native.exe`；启动后台后从托盘打开界面。关闭界面不停止采集，托盘“退出”停止全部进程。

## 构建与发布

在 Windows PowerShell 中，从仓库目录执行：

```powershell
./scripts/Verify-Native.ps1
./scripts/Publish-Native.ps1
```

默认输出 `artifacts/native-win-x64`，需要 .NET 10 Desktop 和 ASP.NET Core 运行时；可用 `-SelfContained` 生成包含运行时的包。请使用独立的空输出目录，不能覆盖正在运行的程序。发布脚本不操作已有数据库或安装目录。

API 客户端无 WPF、业务模块或 EF Core 依赖。原生 UI 仅引用 API 客户端；后台不引用 WPF。替换前端只需实现本地 API 消费者和下述启动协议，不需要重写采集器或直接访问 SQLite。

## 启动协议

父进程重定向界面的标准输入输出，以逐行 UTF-8 JSON 通信。令牌只经管道传递，禁止写入启动参数、URL、磁盘或日志；标准输出仅承载控制消息，诊断不得包含令牌。

- 后台→界面：`initialize`（baseUrl、token），`activate`，`shutdown`，`credential`（token）。
- 界面→后台：`reauthenticate`。`navigate`（page 0–6）用于隔离自动验证，不承载业务数据。
- token 是 60 秒有效的一次性原生启动凭据；通过 `POST /api/auth/native-session` 兑换 12 小时 Bearer 会话。原生令牌与旧 Web Cookie、扩展令牌的命名空间和权限分开。
- 界面退出后撤销其全部凭据与会话；父进程退出或管道 EOF 时界面退出。会话过期重新兑换，GET 可重试一次，修改和导入不自动重放。
- 所有业务数据仍经本机 HTTP API。新前端启动时检查 `/api/system/info` 的协议版本，当前为 1，同时检查分页、图标、配对和导入导出等必需能力。

协议新增接口的 OpenAPI 说明见 `native-api.openapi.json`。现有业务 API 路径、导出 JSON 格式与设置结构保持兼容。新分页路径是显式选择的附加接口，旧数组响应不改变；分页按当前数据库视图读取，持续采集/删除期间不是冻结快照。

## 界面与资源规则

主窗口默认 960×700 DIP，最小 760×560 DIP。七个导航页面完整覆盖概览、详细统计、应用、网站、分类、数据、设置；保留中英文、系统/浅/深主题、扩展配对、图标和原格式导入导出。

样式以 WPF Fluent 资源字典为基础，集中覆盖尺寸与颜色，禁用 Fluent 默认背景材质。图表使用 DrawingVisual，表格虚拟化，图标按显示尺寸解码、16 MiB LRU 缓存。页面离开时取消请求并释放数据；今日统计每 30 秒刷新，历史页面和最小化窗口不轮询。

时间线按逻辑日读取，每页 500 条精确记录，保留前后页和逐日导航。图表只显示可见对象行，可上下移动；同对象占用同一像素的片段仅在绘制时合并，明细不近似。应用与网站统计分别展示，不能相加作为总屏幕时长。

新建配置一天起点为 0 点，已有记录和设置不迁移；设置页允许 0–23 点和自然日快捷切换。更改日界线重新查询统计，不重写原始会话。主题/窗口位置存储在 `NativeWindow.json`，业务设置由后台持有。双进程仍共用原后台 Data 目录，UI 不创建第二套数据库。

本轮验证结果与尚未完成的人工验收见 [验证记录](NATIVE_VALIDATION.md)。

## 验证与回退

`Verify-Native.ps1` 执行严格构建、现有架构/基础/安全回归和 WPF 七页截图测试。界面截图测试只使用内存 HTTP 模拟响应，不启动后台、读取真实记录或修改开机启动。

隔离生命周期和旧版性能对照工具：

```powershell
dotnet build ./tests/ScreenTimeTracker.NativeHarness -c Release
./tests/ScreenTimeTracker.NativeHarness/bin/Release/net10.0-windows/ScreenTimeTracker.NativeHarness.exe
```

此工具在随机 loopback 端口启动模拟 API，不注册真实采集器；调用正式界面启动器做 20 次重复打开/关闭，再分别对 WPF 与现有前端 dist 的 Photino 窗口执行概览、5000 段时间线各 5 次测量。结果位于 `artifacts/native-validation/performance.json`。重复资源对照可用 `--benchmark-only`，崩溃和管道 EOF 检查用 `--lifecycle-extra`。详细统计对照选中同一虚构对象；模拟认证时钟在样本间推进，避免网页登录限流干扰测量。内存按界面进程树汇总；共享模拟后台不计入界面差值，工作集包含共享页，不能解释为独占物理内存。CPU 是短时间空闲采样，打开耗时截至窗口句柄可见，不等于内容完全绘制。GPU 计数器不可用时记录原因；DPI、真实显示器和 Explorer 恢复须另行验收。性能对照必须确认两种界面成功请求并显示业务数据，错误窗口或未加载网页的结果不得用于比较。

旧源码继续位于 `src/frontend`，Photino 启动器保存在 `LegacyPhotinoUIManager.cs`。回退构建使用：

```powershell
# 先按旧 README 构建 frontend/dist，再发布到另一个目录。
dotnet publish ./src/Hosts/ScreenTimeTracker.Desktop -c Release -r win-x64 -p:LegacyWebUI=true -o ./artifacts/legacy-win-x64
```

旧、新包均不要运行在同一实际数据目录中做 A/B 测试。测试与发布产物不提交 Git；功能实现、接口契约和验证脚本作为源码保留。
