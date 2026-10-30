# -*- coding: utf-8 -*-
"""
培训文档核验常量表（check_docs.py 读这里）。

干什么：把"6 份文档该长什么样"集中一处——文档名/H1 标题/必含截图/
  客户向禁词/dev 专属图，一处改处处生效。
为什么单独成文件：写作稿与核验口径分离，改断言不动脚本主体。

怎么改：
  * 新增/改名文档 → DOC_NAMES / DOC_TITLES / REQUIRED_IMGS 同步；
  * 权限口径变化（dev 专属窗变了） → DEV_ONLY_IMGS / CUSTOMER_BANNED；
  * 截图白名单以 docs/images 实际文件为准（IMG_WHITE 全集）。
"""

import re

# ---- 6 份文档（md 名 = pdf 同名）----
DOC_NAMES = [
    "固化程控_操作员手册.md",
    "固化程控_客户技术工艺手册.md",
    "固化程控_内部手册.md",
    "整线网关_操作员手册.md",
    "整线网关_客户技术工艺手册.md",
    "整线网关_内部手册.md",
]

# 各份 md 的 H1 首行必须精确等于（防错复制错标题）
DOC_TITLES = {
    "固化程控_操作员手册.md": "# 固化程控 操作员手册",
    "固化程控_客户技术工艺手册.md": "# 固化程控 客户技术工艺手册",
    "固化程控_内部手册.md": "# 固化程控 内部手册",
    "整线网关_操作员手册.md": "# 整线网关 操作员手册",
    "整线网关_客户技术工艺手册.md": "# 整线网关 客户技术工艺手册",
    "整线网关_内部手册.md": "# 整线网关 内部手册",
}

# ---- 截图白名单（docs/images 实际落盘；V0.0.31/32 重拍后 15 张）----
# 程序① 9 张（cs-*）+ 程序② 6 张（gw-*）；顺序约定：前 9 张属程序①，其后属程序②
# （check_docs.py 内部版"全量"断言按此切片，改动须同步）。
IMG_WHITE = [
    "cs-main.png", "cs-main-dark.png", "cs-main-light.png",
    "cs-login.png", "cs-login-pwd.png", "cs-workorder.png",
    "cs-pointtable.png", "cs-policy.png", "cs-iotest.png",
    "cs-operators.png", "cs-mespost.png", "cs-log.png",
    "gw-main.png", "gw-login.png", "gw-log.png",
    "gw-debug.png", "gw-changepwd.png", "gw-config.png",
]

# dev（最高权限）专属窗截图——客户向文档严禁出现。
# gw-config.png = 网关配置窗（仅 dev 可见入口）；
# gw-changepwd.png 为 admin 登录态拍摄（顶栏无"网关设置"钮），客户向可用。
DEV_ONLY_IMGS = ["gw-config.png"]

# 客户向 4 份的文本禁词（权限红线：dev 专属页零出现）
CUSTOMER_BANNED = [
    "网关设置",      # 程序② dev 专属顶栏按钮
    "网关配置",      # 网关配置窗标题（dev 专属）
    "GatewayConfig", # 类名泄露
    "开发者面板",
]

# 各份文档必含截图（写了就必须有；内部版加"全集"断言在 check_docs 里）
REQUIRED_IMGS = {
    "固化程控_操作员手册.md": [
        "cs-main.png", "cs-main-dark.png", "cs-login.png", "cs-login-pwd.png",
        "cs-workorder.png",
    ],
    "固化程控_客户技术工艺手册.md": [
        "cs-main.png", "cs-policy.png", "cs-pointtable.png",
        "cs-mespost.png", "cs-iotest.png", "cs-workorder.png",
    ],
    "固化程控_内部手册.md": IMG_WHITE[:12],
    "整线网关_操作员手册.md": [
        "gw-main.png", "gw-login.png", "gw-log.png", "gw-debug.png",
        "gw-changepwd.png",
    ],
    "整线网关_客户技术工艺手册.md": [
        "gw-main.png", "gw-login.png", "gw-log.png", "gw-debug.png",
        "gw-changepwd.png",
    ],
    "整线网关_内部手册.md": IMG_WHITE[12:],
}

# 客户面向份（禁词 + DEV_ONLY 图两道闸都跑）
CUSTOMER_DOCS = {
    "固化程控_操作员手册.md",
    "固化程控_客户技术工艺手册.md",
    "整线网关_操作员手册.md",
    "整线网关_客户技术工艺手册.md",
}

# 常见笔误拦截：md 里引用 images/xxx.png 但 xxx 不在白名单 → FAIL
_IMG_RX = re.compile(r"!\[[^\]]*\]\((images/[A-Za-z0-9_\-]+\.png)\)")

def img_refs(md_text: str):
    """收集 markdown 图片引用文件名（统一 images/ 前缀口径）。"""
    return [m.split("/")[-1] for m in _IMG_RX.findall(md_text)]
