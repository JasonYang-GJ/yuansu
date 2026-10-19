# GitHub 仓库合并说明

日期：2026-10-20。

元枢与超级灵动岛已经合并为一个 Windows 产品。GitHub 的当前代码、构建说明和后续问题反馈统一使用 [JasonYang-GJ/yuansu](https://github.com/JasonYang-GJ/yuansu)。保留现有仓库地址、提交历史和冻结标签。

| 内容 | 当前入口 |
| --- | --- |
| 元枢主程序、Host、模型和权限逻辑 | 本仓库 `src/ScreenGuide.*` |
| 内置灵动岛、前端与原生组件测试 | [src/Yuanshu.Island](../src/Yuanshu.Island) |
| 完整产品构建与安装更新 | [统一构建指南](UNIFIED_PRODUCT_BUILD.md) |
| 合并和真实使用验收范围 | [合并验收](UNIFIED_PRODUCT_ACCEPTANCE.md)、[真实使用](UNIFIED_PRODUCT_REAL_USAGE.md) |
| 旧独立项目历史 | [JasonYang-GJ/yuanshu-island](https://github.com/JasonYang-GJ/yuanshu-island)，仍为私有仓库，需访问权限 |

旧仓库保留原文件与历史，只补充迁移入口；不再作为当前产品的开发或构建来源。原 `36df1f47287227e74b50f1335502b718850df9bb` 提交及当时未提交的整合修改是导入起点，详见 [导入清单](imports/ISLAND_SOURCE_IMPORT.json)。清单描述导入当时的文件，不要求后续组件修改继续匹配旧摘要。原私有仓库完整历史不导入公开仓库。

本次同步基于元枢 `e5796b84469a6b9b051e457c6e94de1b0cbce213`。公开前，逐项复核 R10 构建清单的 598 个源文件，全部匹配，原清单 SHA-256 为 `0E1F0953B779040DCD85C44D43A4C9ED6C54D0CC838F78A9FFE1D34B0D81AF29`。因此沿用这份精确源码的既有构建和验收证据，不把上传操作描述成重新完成真人或安装测试。Git 可能按仓库规则规范化文本换行；这不改变历史安装包身份。

公开内容只包含源码、测试、文档、必要图标、锁文件和第三方许可。真实密钥、用户数据、录音、截图、运行日志、构建缓存和安装二进制保留在仓库外。新增调查文档中的本机绝对路径改为 `<private-evidence>` 或 `<private-candidates>` 占位符；证据目录名称和结果保留以便本机追溯。

0.8.0 仍为合并候选；本次不创建版本冻结标签、不发布安装包，也不添加项目级开源许可证。
