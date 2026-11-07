# -*- coding: utf-8 -*-
"""
PCL 主页本地服务 · exe 入口
- 单实例：8848 已在服务则直接退出
- 无控制台时把输出写入 exe 同目录 server.log
"""
import os
import socket
import sys
import time

import motd_local_server as srv

def app_dir():
    if getattr(sys, "frozen", False):
        return os.path.dirname(os.path.abspath(sys.executable))
    return os.path.dirname(os.path.abspath(__file__))


def already_running(port):
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.settimeout(0.6)
    try:
        s.connect(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def log_line(base, msg):
    """崩溃自检用：单文件 exe 没有控制台，任何退出原因都写进 launcher.log"""
    try:
        with open(os.path.join(base, "server_crash.log"), "a", encoding="utf-8") as f:
            f.write(time.strftime("%Y-%m-%d %H:%M:%S ") + msg + "\n")
    except OSError:
        pass


def main():
    base = app_dir()
    port = int(os.environ.get("PCL_MOTD_PORT", getattr(srv, "HTTP_PORT", 8848)))
    srv.HTTP_PORT = port

    if already_running(port):
        log_line(base, "检测到 {0} 端口已有服务，本次启动直接退出".format(port))
        return 0

    if sys.stdout is None or sys.stderr is None:
        log = open(os.path.join(base, "server.log"), "a", encoding="utf-8", buffering=1)
        sys.stdout = log
        sys.stderr = log

    try:
        srv.main()
        log_line(base, "服务正常退出")
    except SystemExit:
        raise
    except Exception:
        import traceback
        log_line(base, "服务异常退出：\n" + traceback.format_exc())
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
