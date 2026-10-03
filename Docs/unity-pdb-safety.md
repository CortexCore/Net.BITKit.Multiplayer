# Unity Portable PDB 安全修复

2026-09-28 的 NPC EditMode 测试捕获异常时暴露了 ILPP 的符号缺陷：修改后的 DLL 有947个方法，但新生成的空 PDB 的 MethodDebugInformation 表为0行。Unity Mono 的异常堆栈查找进入 `mono_ppdb_lookup_location`，在 `metadata.c:1381` 触发 `idx < t->rows` 原生断言。不是普通 C# 异常，也不是场景几何损坏。

修复位于共享 `Weaver.EnsureSafePortableSymbols`，Unity ILPP 写出 PE/PDB 前调用它：

- 为没有序列点的方法补充 hidden debug entry。
- 最后的生成 sentinel 方法保证调试表覆盖最后一个 MethodDef，包括尾部 abstract/interface 方法。
- 继续输出与修改后的 DLL 成对的 Portable PDB，不把旧 PDB 附回新程序集。
- 这些是合法 hidden 符号，**原始源码行映射仍待实现**。

验证：

1. 新 .NET `PortableSymbolsCoverAllMethodsIncludingTrailingInterfaces` 回归通过。
2. 原 Unity DLL/PDB 离线审计得到947/0、430/0；修复副本为948/948、431/431（多出的1个方法是 sentinel）。
3. Unity 自带的 Mono 控制台进程成功执行异常抛出、TaskCompletionSource.TrySetException 和堆栈读取。
4. 重启并导入后的 Unity 实际产物表行数匹配；原 NPC 测试能够正常返回托管断言失败，修正测试生命周期/完成时序后通过。

此修复已经在主库源码。不得删除 hidden rows 来减少 PDB 大小；新增 ILPP 变换需要继续检查 MethodDef 与 MethodDebugInformation 的覆盖关系。
