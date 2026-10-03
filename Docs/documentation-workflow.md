# 文档维护与 GitBook

## 单一文档源

仓库 Markdown 是源文件；GitHub、IDE、AI 和 GitBook 使用同一套内容，不再另维护网站版说明。

根 `.gitbook.yaml` 将单个文档空间的内容根设为 `Docs/`：

```yaml
root: ./Docs/
structure:
  readme: README.md
  summary: SUMMARY.md
```

配置和目录已准备，但没有创建/发布 GitBook 站点，也没有替用户提交、推送或连接账号。后续启用 Git Sync 时，将此仓库根作为配置入口；若 GitBook 使用多 space 映射，按平台当时的映射目录调整 root，而不是重复叠加 `Docs/Docs/`。

格式依据：[GitBook Content configuration](https://gitbook.com/docs/docs-as-code/git-sync/content-configuration.md)。`SUMMARY.md` 中每个页面只出现一次。这里只配置内容读取，不调用旧 GitBook CLI，也不暗示已经自动生成完整 API reference。

## 哪些页面应更新

| 改动 | 一并维护 |
| --- | --- |
| RPC/返回值/错误/上下文 | `api-contracts.md`、`typed-rpc-guide.md` |
| 帧、ID、兼容性 | `current-status.md`、相关协议指南 |
| 传输/借用/释放 | `api-contracts.md`、`transport-guide.md` |
| Unity 阶段通过 | `unity-integration-plan.md` 的阶段记录、`current-status.md`、AI 导航 |
| 验收/性能 | 新增日期明确的证据页，更新当前状态的最新链接；保留旧结果 |
| 文件移动/新增关键入口 | `ai-integration-handoff.md`、`SUMMARY.md` |

架构约束的改变先明确记录在 `architecture.md`，不要仅在历史实验页的末尾补一句相反结论。

## 历史文件规则

- 历史文件不作为默认导航入口；统一通过 `history-index.md` 追溯。
- 不能把上一个阶段的成功测试数改成“本轮又验证过”。
- 不根据 .NET build 成功填写 Unity 编译、Player、IL2CPP 或场景验收为成功。
- 性能数字注明进程/线程、预热、真假 wire、窗口和测试环境；0 B 断言不代表所有 DTO/异步/引擎路径零 GC。

## 修改后的轻量检查

1. `SUMMARY.md` 的目标文件存在、大小写正确、没有重复条目。
2. 当前页面之间的相对文档链接可达；源码路径以代码文本表示，避免 GitBook 试图把 Docs 外的源码当页面。
3. `Artifacts/` 是本地证据位置，不当成已上传的网站附件。
4. 对照指定源码符号检查接口/版本/依赖；链接尽量指文件和符号，不依赖会变动的行号。
5. 纯文档轮次不需要重跑 Unity 或整套 runtime 测试；如果改了代码，按任务对应验证路径执行。
