# -*- coding: utf-8 -*-
"""把 Unity 编辑器窗口强制拉到前台（唤醒后台沉睡的编辑器）。
用法: python Tuning/focus_unity.py   → 成功输出 FOCUS_OK"""
import ctypes, sys, time

u = ctypes.windll.user32
EnumProc = ctypes.WINFUNCTYPE(ctypes.c_bool, ctypes.c_void_p, ctypes.c_void_p)
targets = []

def cb(hwnd, lp):
    if u.IsWindowVisible(hwnd):
        n = u.GetWindowTextLengthW(hwnd)
        buf = ctypes.create_unicode_buffer(n + 1)
        u.GetWindowTextW(hwnd, buf, n + 1)
        if 'Unity' in buf.value and 'Unity Hub' not in buf.value:
            targets.append(hwnd)
    return True

u.EnumWindows(EnumProc(cb), None)
if not targets:
    print('NO_UNITY_WINDOW'); sys.exit(1)

hwnd = targets[0]
u.ShowWindow(hwnd, 9)                                   # SW_RESTORE
u.SetWindowPos(hwnd, -1, 0, 0, 0, 0, 0x0003)            # 置顶
time.sleep(0.3)
u.SetWindowPos(hwnd, -2, 0, 0, 0, 0, 0x0003)            # 取消置顶
u.keybd_event(0x12, 0, 0, 0)                            # ALT down（骗过前台锁）
time.sleep(0.05)
r = u.SetForegroundWindow(hwnd)
u.keybd_event(0x12, 0, 2, 0)                            # ALT up
time.sleep(0.4)
ok = u.GetForegroundWindow() == hwnd
print('FOCUS_OK' if ok else 'FOCUS_FAIL(%s)' % r)
sys.exit(0 if ok else 2)
