# 保留的未交付实验

`unshipped-native-focus-wait.patch` 保留此前工作区对搜索框聚焦等待的实验改动。它不是本次合并新开发的功能，也没有成为用户实际使用版本的通过门禁。

合并包从源码完整构建，`DesktopAutomationServices.cs` 采用此前已交付 `assistant-metadata-results-20261017` 的原源码：SHA-256 `887b38a5838abd65f5a5b1b65c0465453f0f1bfe5dc8e8f619807a297d12e756`。实验的 `BoundSearchFocusWait.cs` 与其测试继续保留，但不被这个已交付路径调用。完整实验文件另在本批仓库外证据目录备份；没有删除原调查记录。

恢复实验时应单独评估和验证，不把合并安装验收当成搜索性能验证。
