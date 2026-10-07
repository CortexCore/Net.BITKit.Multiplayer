# LiteNetLib 独立仓库提取（2026-10-07）

## 结构与来源

- 独立仓库：同级 `Net.BITKit.Multiplayer.LiteNetLib`；`Src/package.json` 是 UPM 根，源码/asmdef 平铺在 `Src/`，.NET `.csproj`/`.slnx` 位于仓库根。
- 最新适配器从主库提取，源码不改动，保留 UniTask 及已有 GUID。旧 `feature/litenetlib-direct` worktree 比主库旧，只作为完整保留的历史备份，路径为 `Net.BITKit.Multiplayer.LiteNetLib.LegacyWorktree`。
- 扩展根项目引用主库 Core、LiteNetLib 1.3.5、UniTask 2.5.10，编译 `Src/**/*.cs`；主库 Core/native Transport 不反向引用扩展。
- 主库 solution/Godot Session/LiteNetLib 集成测试引用同级扩展工程。扩展独立测试不引用 Godot；主库集成测试链接其 `Tests/DirectTests.cs`，追加自己的 wrapper 测试。
- 已初始化独立 Git 的 `main` 分支；未提交、配置 remote 或推送。许可证与 GitHub 发布方式留待下一步决定。

## 验证证据

.NET 实际验证：

```powershell
# 在独立扩展仓库
dotnet build Net.BITKit.Multiplayer.LiteNetLib.csproj -c Release --nologo -m:1
dotnet test Net.BITKit.Multiplayer.LiteNetLib.slnx -c Release --nologo -m:1
# 在主库
dotnet build Net.BITKit.Multiplayer.slnx -c Release --nologo -m:1
dotnet test Tests/LiteNetLibTests/LiteNetLibTests.csproj -c Release --no-build --nologo
```

- 独立扩展 netstandard2.1/net8.0 构建成功，独立测试 **12/12**；主库完整 solution 构建成功，集成测试 **15/15**（其中 12 项链接独立适配器测试，3 项是主库 runtime/wrapper 集成覆盖）。无失败或跳过。
- 只有主库原有 `RoomTransportHub.cs` CS0067 warning。未重复运行与结构调整无关的全部测试或 Godot 多进程样例。
- 四个提取文件（源码、asmdef 和各自 `.meta`）与提取前主库 HEAD 的 Git blob 哈希一致。
- Roslyn MCP 加载主库和独立扩展均被服务器 sanctioned-root 限制拒绝；实际 dotnet 编译/测试结果如上。

Unity：Project B 2022.3.62f3 从 Play 退出后执行 MCP `request_recompile`，恢复事件 Success，编译错误读回为空。PackageManager 实际读回主库及 Unity 扩展 0.1.0 的本地 `Src` 路径；CompilationPipeline 读回 LiteNetLib 程序集不存在。此结果仅说明主库提取后的既有宿主编译通过，不表示 Unity LiteNetLib 安装、会话或 Player/IL2CPP 已验收；没有再次进入 Play Mode。
