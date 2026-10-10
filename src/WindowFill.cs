// 창 빈 공간 채우기 (Window Fill).
//
// 화면에서 다른 창이 없는 빈 직사각형 후보를 모두 구한 뒤, 지금 창과 가장 많이 겹치는
// 후보로 창의 위치와 크기를 맞춘다.
//
// [경량화] 순수 Win32 API만 사용하며 WinForms/System.Drawing을 로드하지 않는다.
//   유휴 시 상시 폴링이 없으며, 드래그 중에만 35ms 간격으로 빈 영역 미리보기를 계산한다.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OnTopPasskey
{
    internal static class WindowFill
    {
        public const int HotkeyId = 2;
        // Ctrl+Alt+Shift+F11. 마우스 도움말(F12)과 통일감 있게 배치하여 마우스 버튼 등에 할당하기 용이하다.
        public const int HotkeyMods = 0x0001 | 0x0002 | 0x0004 | 0x4000;   // ALT | CONTROL | SHIFT | NOREPEAT
        public const int HotkeyVk = 0x7A;                                   // VK_F11
        public const string HotkeyText = "Ctrl+Alt+Shift+F11";

        private const int MinDimension = 150; // 너비 또는 높이가 150px 미만인 너무 작은 빈 공간은 버린다.

        // 창별 되돌리기(토글) 기록
        private static readonly Dictionary<IntPtr, Native.RECT> _originalBounds = new Dictionary<IntPtr, Native.RECT>(8);
        private static readonly Dictionary<IntPtr, Native.RECT> _lastFilledBounds = new Dictionary<IntPtr, Native.RECT>(8);

        // 창 드래그 이동 감지용
        private static IntPtr _dragHwnd = IntPtr.Zero;
        private static Native.RECT _dragStartRect;
        private static uint _dragStartDpi;
        private static List<Native.RECT> _dragObstacles;
        private static bool _dragAutoProcess;
        internal const int PreviewTimerId = 3; // 본체의 재시도(1), 안전망(2) 타이머와 분리
        private static IntPtr _previewTimerOwner;

        // 미리보기 창 하나를 재사용한다. 소유자를 두면 대상 위로 올라가므로 소유 없는 팝업이다.
        private const string PreviewClassName = "OnTopPasskey.FillPreview";
        private static readonly Native.WndProcDelegate _previewWndProc = PreviewWndProc;
        private static bool _previewClassRegistered;
        private static IntPtr _previewHwnd;
        private static bool _previewVisible;
        private static Native.RECT _previewRect;

        // 힙 할당을 방지하기 위해 struct 로 정의
        private struct Interval : IComparable<Interval>
        {
            public int Start;
            public int End;
            public Interval(int s, int e) { Start = s; End = e; }
            public int CompareTo(Interval other) { return Start.CompareTo(other.Start); }
        }

        private static bool IsDesktopOrTaskbar(string className)
        {
            return className == "Progman" || className == "WorkerW" ||
                   className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd";
        }

        /// <summary>
        /// 빈 직사각형 후보를 찾고 현재 창과 가장 많이 겹치는 최적의 빈 영역을 구하는 순수 함수.
        /// </summary>
        public static bool Compute(Native.RECT workArea, IList<Native.RECT> obstacles, Native.RECT current, out Native.RECT result)
        {
            result = default(Native.RECT);

            // 1. 작업 영역 안으로 장애물 사각형을 자른다.
            var clippedObstacles = new List<Native.RECT>(obstacles.Count);
            var xCoords = new List<int>(obstacles.Count * 2 + 2);
            xCoords.Add(workArea.left);
            xCoords.Add(workArea.right);

            for (int i = 0; i < obstacles.Count; i++)
            {
                Native.RECT o = obstacles[i];
                int cl = Math.Max(workArea.left, o.left);
                int ct = Math.Max(workArea.top, o.top);
                int cr = Math.Min(workArea.right, o.right);
                int cb = Math.Min(workArea.bottom, o.bottom);
                if (cr > cl && cb > ct)
                {
                    clippedObstacles.Add(new Native.RECT { left = cl, top = ct, right = cr, bottom = cb });
                    xCoords.Add(cl);
                    xCoords.Add(cr);
                }
            }

            // 2. X 좌표 후보들을 정렬하고 중복을 제거한다.
            xCoords.Sort();
            var uniqueX = new List<int>(xCoords.Count);
            for (int i = 0; i < xCoords.Count; i++)
            {
                if (i == 0 || xCoords[i] != xCoords[i - 1])
                    uniqueX.Add(xCoords[i]);
            }

            long bestOverlap = -1;
            long bestArea = -1;
            Native.RECT bestCandidate = default(Native.RECT);
            bool found = false;

            // GC 압박을 없애기 위해 리스트 인스턴스를 재사용
            var yIntervals = new List<Interval>(clippedObstacles.Count);
            var mergedY = new List<Interval>(clippedObstacles.Count);

            // 3. 모든 (x1 < x2) 쌍마다 X 구간과 겹치는 장애물들의 Y 구간을 모으고 빈 Y 틈을 찾는다.
            for (int i = 0; i < uniqueX.Count; i++)
            {
                int x1 = uniqueX[i];
                for (int j = i + 1; j < uniqueX.Count; j++)
                {
                    int x2 = uniqueX[j];
                    int w = x2 - x1;
                    if (w < MinDimension) continue;

                    yIntervals.Clear();
                    for (int k = 0; k < clippedObstacles.Count; k++)
                    {
                        Native.RECT o = clippedObstacles[k];
                        if (o.left < x2 && o.right > x1)
                        {
                            yIntervals.Add(new Interval(o.top, o.bottom));
                        }
                    }

                    yIntervals.Sort();
                    mergedY.Clear();
                    if (yIntervals.Count > 0)
                    {
                        Interval cur = yIntervals[0];
                        for (int k = 1; k < yIntervals.Count; k++)
                        {
                            if (yIntervals[k].Start <= cur.End)
                            {
                                cur.End = Math.Max(cur.End, yIntervals[k].End);
                            }
                            else
                            {
                                mergedY.Add(cur);
                                cur = yIntervals[k];
                            }
                        }
                        mergedY.Add(cur);
                    }

                    int curY = workArea.top;
                    for (int k = 0; k < mergedY.Count; k++)
                    {
                        if (mergedY[k].Start > curY)
                        {
                            int gapTop = curY;
                            int gapBottom = mergedY[k].Start;
                            int h = gapBottom - gapTop;
                            if (h >= MinDimension)
                            {
                                EvaluateCandidate(x1, gapTop, x2, gapBottom, current,
                                    ref bestOverlap, ref bestArea, ref bestCandidate, ref found);
                            }
                        }
                        curY = Math.Max(curY, mergedY[k].End);
                    }
                    if (curY < workArea.bottom)
                    {
                        int gapTop = curY;
                        int gapBottom = workArea.bottom;
                        int h = gapBottom - gapTop;
                        if (h >= MinDimension)
                        {
                            EvaluateCandidate(x1, gapTop, x2, gapBottom, current,
                                ref bestOverlap, ref bestArea, ref bestCandidate, ref found);
                        }
                    }
                }
            }

            if (found)
            {
                result = bestCandidate;
                return true;
            }
            return false;
        }

        private static void EvaluateCandidate(int x1, int y1, int x2, int y2, Native.RECT current,
            ref long bestOverlap, ref long bestArea, ref Native.RECT bestCandidate, ref bool found)
        {
            int ox1 = Math.Max(x1, current.left);
            int oy1 = Math.Max(y1, current.top);
            int ox2 = Math.Min(x2, current.right);
            int oy2 = Math.Min(y2, current.bottom);

            long overlap = 0;
            if (ox2 > ox1 && oy2 > oy1)
            {
                overlap = (long)(ox2 - ox1) * (long)(oy2 - oy1);
            }

            long area = (long)(x2 - x1) * (long)(y2 - y1);

            // (대상 창과 겹치는 넓이, 후보 넓이) 순으로 최댓값을 고른다
            if (overlap > bestOverlap || (overlap == bestOverlap && area > bestArea))
            {
                bestOverlap = overlap;
                bestArea = area;
                bestCandidate = new Native.RECT { left = x1, top = y1, right = x2, bottom = y2 };
                found = true;
            }
        }

        /// <summary>
        /// 화면의 장애물 창들을 수집한다. (최대화/최소화/숨김/투명/소유된 팝업 창 등은 제외)
        /// </summary>
        public static List<Native.RECT> CollectObstacles(IntPtr targetHwnd, Native.RECT workArea)
        {
            var obstacles = new List<Native.RECT>();
            var sbClass = new StringBuilder(64);

            Native.EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                if (hwnd == targetHwnd || hwnd == App.Hwnd || hwnd == _previewHwnd) return true;
                if (!Native.IsWindowVisible(hwnd)) return true;
                if (Native.IsIconic(hwnd)) return true; // 최소화된 창 제외

                // 최대화된 창은 배경으로 취급하여 장애물에서 제외
                if (Native.IsZoomed(hwnd)) return true;

                // 툴 윈도우 및 클릭 통과 오버레이 제외
                long exStyle = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE);
                if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if ((exStyle & Native.WS_EX_TRANSPARENT) != 0) return true;

                // 대상 창이 (간접으로라도) 소유한 팝업 제외. 소유 체인 중간에 대상이 있을 수 있다.
                for (IntPtr o = Native.GetWindow(hwnd, Native.GW_OWNER); o != IntPtr.Zero;
                     o = Native.GetWindow(o, Native.GW_OWNER))
                    if (o == targetHwnd) return true;

                // 숨겨진(cloaked) 창 제외 (다른 가상 데스크톱 창, UWP 백그라운드 등)
                int cloaked = 0;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0)
                    return true;

                // 바탕화면 및 작업 표시줄 제외
                sbClass.Length = 0;
                Native.GetClassName(hwnd, sbClass, sbClass.Capacity);
                if (IsDesktopOrTaskbar(sbClass.ToString())) return true;

                // FillIgnoreProcesses 에 등록된 프로세스 제외 (설정된 경우에만 프로세스명 조회)
                if (Config.FillIgnoreProcesses.Count > 0)
                {
                    string proc = App.ProcessNameOf(hwnd);
                    if (!string.IsNullOrEmpty(proc))
                    {
                        for (int i = 0; i < Config.FillIgnoreProcesses.Count; i++)
                        {
                            if (string.Equals(proc, Config.FillIgnoreProcesses[i], StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                    }
                }

                // DWM 보이는 경계 얻기 (실패하거나 크기가 유효하지 않으면 GetWindowRect 로 폴백)
                Native.RECT visibleRect;
                if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out visibleRect, Marshal.SizeOf(typeof(Native.RECT))) != 0 ||
                    visibleRect.right <= visibleRect.left || visibleRect.bottom <= visibleRect.top)
                {
                    if (!Native.GetWindowRect(hwnd, out visibleRect)) return true;
                }

                // 작업 영역 밖의 창은 Compute 가 잘라서 버린다. 1px 만 걸쳐도 벽이다.
                obstacles.Add(visibleRect);
                return true;
            }, IntPtr.Zero);

            return obstacles;
        }

        public static bool GetVisibleBounds(IntPtr hwnd, out Native.RECT visibleRect, out Native.RECT winRect)
        {
            visibleRect = default(Native.RECT);
            winRect = default(Native.RECT);
            if (!Native.GetWindowRect(hwnd, out winRect)) return false;

            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out visibleRect, Marshal.SizeOf(typeof(Native.RECT))) != 0 ||
                visibleRect.right <= visibleRect.left || visibleRect.bottom <= visibleRect.top)
            {
                visibleRect = winRect;
            }
            return true;
        }

        /// <summary>
        /// 트레이 메뉴용. 트레이 아이콘을 누르면 작업 표시줄이 포그라운드가 되므로, 그때는
        /// Z-order 상 가장 위의 일반 창을 대상으로 삼는다. 단축키 경로는 이 대체를 쓰지 않는다.
        /// </summary>
        public static IntPtr MenuTarget(IntPtr candidate)
        {
            if (IsValidTarget(candidate)) return candidate;

            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr lParam)
            {
                if (IsValidTarget(h))
                {
                    found = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static bool IsValidTarget(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            if (hwnd == App.Hwnd || hwnd == _previewHwnd) return false;
            if (!Native.IsWindowVisible(hwnd)) return false;
            if (Native.IsIconic(hwnd) || Native.IsZoomed(hwnd)) return false;

            long exStyle = Native.GetWindowLongSafe(hwnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0) return false;
            if ((exStyle & Native.WS_EX_TRANSPARENT) != 0) return false;

            int cloaked = 0;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0)
                return false;

            var sbClass = new StringBuilder(64);
            Native.GetClassName(hwnd, sbClass, sbClass.Capacity);
            if (IsDesktopOrTaskbar(sbClass.ToString())) return false;

            Native.RECT r;
            if (!Native.GetWindowRect(hwnd, out r)) return false;
            if (r.right - r.left < 50 || r.bottom - r.top < 50) return false;

            return true;
        }

        /// <summary>
        /// 창을 빈 공간으로 채운다. 이미 채워진 상태에서 allowToggle=true 이면 원래 크기로 복원한다.
        /// </summary>
        public static void Fill(IntPtr hwnd, bool allowToggle, bool atCursor = false)
        {
            try
            {
                // 최대화·최소화 등 부적합한 창이면 다른 창으로 대체하지 않고 그냥 끝낸다.
                if (!IsValidTarget(hwnd)) return;

                Native.RECT currentVisible;
                Native.RECT currentWinRect;
                if (!GetVisibleBounds(hwnd, out currentVisible, out currentWinRect)) return;

                // 되돌리기(토글): 채운 상태 그대로면 원래 크기로 복원
                Native.RECT lastFilled, orig;
                if (allowToggle && _lastFilledBounds.TryGetValue(hwnd, out lastFilled)
                    && Near(currentWinRect, lastFilled) && _originalBounds.TryGetValue(hwnd, out orig))
                {
                    if (Native.SetWindowPos(hwnd, IntPtr.Zero,
                            orig.left, orig.top, orig.right - orig.left, orig.bottom - orig.top,
                            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                    {
                        _originalBounds.Remove(hwnd);
                        _lastFilledBounds.Remove(hwnd);
                        Log.Write(L.T("창 크기 원래대로 복원: ", "Restored window to original size: ") + App.ProcessNameOf(hwnd));
                    }
                    else
                        Log.Write(L.T("창 복원 실패 (err ", "Window restore failed (err ")
                                  + Marshal.GetLastWin32Error() + ")");
                    return;
                }

                // 장애물 수집 및 최적 빈 영역 계산 (미리보기는 시작 때 수집한 목록을 전달한다.)
                Native.RECT targetVisible;
                if (!TryComputeFill(hwnd, null, currentVisible, atCursor, out targetVisible))
                {
                    Log.Write(L.T("창 채우기: 적합한 빈 공간 없음 (", "Window fill: no suitable empty space found (") + App.ProcessNameOf(hwnd) + ")");
                    return;
                }

                // 이미 해당 위치와 크기에 정확히 맞추어져 있다면 추가 조작 생략
                if (SameRect(currentVisible, targetVisible))
                {
                    return;
                }

                // Windows 10/11 보이지 않는 창 테두리(약 7px) 보정
                int diffLeft = Math.Max(0, currentVisible.left - currentWinRect.left);
                int diffTop = Math.Max(0, currentVisible.top - currentWinRect.top);
                int diffRight = Math.Max(0, currentWinRect.right - currentVisible.right);
                int diffBottom = Math.Max(0, currentWinRect.bottom - currentVisible.bottom);

                int newWinLeft = targetVisible.left - diffLeft;
                int newWinTop = targetVisible.top - diffTop;
                int newWinWidth = (targetVisible.right - targetVisible.left) + diffLeft + diffRight;
                int newWinHeight = (targetVisible.bottom - targetVisible.top) + diffTop + diffBottom;

                if (!Native.SetWindowPos(hwnd, IntPtr.Zero,
                        newWinLeft, newWinTop, newWinWidth, newWinHeight,
                        Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                {
                    Log.Write(L.T("창 채우기 실패 (err ", "Window fill failed (err ")
                              + Marshal.GetLastWin32Error() + "): " + App.ProcessNameOf(hwnd));
                    return;
                }

                // 자동 채우기도 원래 위치를 남겨야 단축키로 되돌릴 수 있다.
                // 창이 최소 크기 등으로 요청을 보정할 수 있으므로 실제 적용된 좌표를 기록한다.
                _originalBounds[hwnd] = currentWinRect;
                Native.RECT applied;
                if (Native.GetWindowRect(hwnd, out applied)) _lastFilledBounds[hwnd] = applied;

                PruneHistory();

                Log.Write(L.T("창 빈 공간 채우기 완료: ", "Window filled into empty space: ")
                          + App.ProcessNameOf(hwnd)
                          + string.Format(" [{0},{1} - {2},{3}]", targetVisible.left, targetVisible.top, targetVisible.right, targetVisible.bottom));
            }
            catch (Exception ex)
            {
                Log.WriteAlways(L.T("창 채우기 오류: ", "Window fill error: ") + ex.Message);
            }
        }

        private static bool Near(Native.RECT a, Native.RECT b)
        {
            return Math.Abs(a.left - b.left) <= 5 && Math.Abs(a.top - b.top) <= 5 &&
                   Math.Abs(a.right - b.right) <= 5 && Math.Abs(a.bottom - b.bottom) <= 5;
        }

        private static bool SameRect(Native.RECT a, Native.RECT b)
        {
            return a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
        }

        // 배율이 다른 디스플레이로 넘어가면 창이 DPI 비율만큼 스스로 크기를 바꾸므로, 그 비율로 환산해 비교한다.
        // ponytail: 앱마다 비클라이언트 영역 환산이 달라 정확히 맞지 않아 DPI가 바뀐 경우만 10% 오차를 허용한다.
        private static bool SameDragSize(IntPtr hwnd, Native.RECT current, Native.RECT start, uint startDpi)
        {
            int width = start.right - start.left;
            int height = start.bottom - start.top;
            if (width <= 0 || height <= 0) return false;
            int cw = current.right - current.left;
            int ch = current.bottom - current.top;
            uint dpi = Native.GetDpiForWindow(hwnd);
            if (startDpi == 0 || dpi == 0 || dpi == startDpi) return cw == width && ch == height;
            double ew = width * (double)dpi / startDpi;
            double eh = height * (double)dpi / startDpi;
            return Math.Abs(cw - ew) <= ew * 0.1 && Math.Abs(ch - eh) <= eh * 0.1;
        }

        // atCursor: 드래그 중에는 창이 아직 원래 디스플레이에 더 많이 걸쳐 있어도 커서가 있는 디스플레이를 채운다.
        private static bool GetWorkArea(IntPtr hwnd, bool atCursor, out Native.RECT workArea)
        {
            workArea = default(Native.RECT);
            Native.POINT pt;
            IntPtr monitor = atCursor && Native.GetCursorPos(out pt)
                ? Native.MonitorFromPoint(pt, Native.MONITOR_DEFAULTTONEAREST)
                : Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return false;
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(monitor, ref mi)) return false;
            workArea = mi.rcWork;
            return true;
        }

        // 실제 채우기는 장애물을 새로 수집하고, 미리보기는 캐시를 써서 같은 계산을 한다.
        private static bool TryComputeFill(IntPtr hwnd, IList<Native.RECT> obstacles,
            Native.RECT currentVisible, bool atCursor, out Native.RECT targetVisible)
        {
            targetVisible = default(Native.RECT);
            Native.RECT workArea;
            if (!GetWorkArea(hwnd, atCursor, out workArea)) return false;
            if (obstacles == null) obstacles = CollectObstacles(hwnd, workArea);
            return Compute(workArea, obstacles, currentVisible, out targetVisible);
        }

        private static bool ShouldAutoFill(bool autoProcess)
        {
            return autoProcess || (Config.FillOnAltDrag && (Native.GetAsyncKeyState(Native.VK_MENU) & 0x8000) != 0);
        }

        private static void PruneHistory()
        {
            if (_originalBounds.Count > 32)
            {
                var dead = new List<IntPtr>();
                foreach (var h in _originalBounds.Keys)
                {
                    if (!Native.IsWindow(h)) dead.Add(h);
                }
                for (int i = 0; i < dead.Count; i++)
                {
                    _originalBounds.Remove(dead[i]);
                    _lastFilledBounds.Remove(dead[i]);
                }
            }
        }

        public static void OnMoveSizeStart(IntPtr hwnd)
        {
            try
            {
                StopDragPreview();
                if (!IsValidTarget(hwnd)) return;
                Native.RECT startRect;
                if (!Native.GetWindowRect(hwnd, out startRect)) return;
                bool autoProcess = IsAutoProcess(hwnd);
                if (!Config.FillOnAltDrag && !autoProcess) return;

                Native.RECT workArea;
                if (!GetWorkArea(hwnd, false, out workArea)) return;
                // 모니터 밖의 장애물도 보관하여 드래그 도중 모니터를 바꿔도 다시 열거하지 않는다.
                _dragObstacles = CollectObstacles(hwnd, workArea);
                _dragHwnd = hwnd;
                _dragStartRect = startRect;
                _dragStartDpi = Native.GetDpiForWindow(hwnd);
                _dragAutoProcess = autoProcess;
                _previewTimerOwner = App.Hwnd;
                if (_previewTimerOwner == IntPtr.Zero ||
                    Native.SetTimer(_previewTimerOwner, new IntPtr(PreviewTimerId), 35, IntPtr.Zero) == IntPtr.Zero)
                {
                    StopDragPreview();
                    return;
                }
                PreviewTick();
            }
            catch (Exception ex)
            {
                StopDragPreview();
                Log.WriteAlways(L.T("창 미리보기 시작 오류: ", "Window preview start error: ") + ex.Message);
            }
        }

        public static void OnMoveSizeEnd(IntPtr hwnd)
        {
            try
            {
                IntPtr dragged = _dragHwnd;
                Native.RECT startRect = _dragStartRect;
                uint startDpi = _dragStartDpi;
                // 실패/다른 HWND의 종료 이벤트도 먼저 정리한다. Fill 전에 반드시 숨기고 타이머를 끈다.
                StopDragPreview();
                if (hwnd == IntPtr.Zero || hwnd != dragged || !Native.IsWindow(hwnd)) return;

                Native.RECT endRect;
                if (!Native.GetWindowRect(hwnd, out endRect)) return;

                // 창 크기가 유지된 경우(=창 이동만 했고, 크기 조절이나 Windows 스냅이 적용된 것이 아님)
                if (!SameDragSize(hwnd, endRect, startRect, startDpi)) return;

                // 단순 클릭(0~3px 미세 클릭)인 경우 오작동 방지
                int movedDist = Math.Abs(endRect.left - startRect.left) + Math.Abs(endRect.top - startRect.top);
                if (movedDist < 4) return;

                // Alt 키를 누른 상태로 드래그를 마쳤거나, FillAutoProcesses 에 등록된 프로세스인 경우
                if (ShouldAutoFill(IsAutoProcess(hwnd)))
                {
                    Fill(hwnd, false, true);
                }
            }
            catch (Exception ex)
            {
                Log.WriteAlways(L.T("창 자동 채우기 오류: ", "Window auto fill error: ") + ex.Message);
            }
        }

        internal static void PreviewTick()
        {
            try
            {
                // KillTimer 뒤 큐에 남은 WM_TIMER는 아무 작업도 하지 않는다.
                if (_dragHwnd == IntPtr.Zero || _dragObstacles == null) return;
                if (!Native.IsWindow(_dragHwnd) || !Native.IsWindowVisible(_dragHwnd) ||
                    Native.IsIconic(_dragHwnd) || Native.IsZoomed(_dragHwnd))
                {
                    StopDragPreview();
                    return;
                }
                Native.RECT visible, winRect, target;
                if (!GetVisibleBounds(_dragHwnd, out visible, out winRect) ||
                    !SameDragSize(_dragHwnd, winRect, _dragStartRect, _dragStartDpi) || !ShouldAutoFill(_dragAutoProcess) ||
                    !TryComputeFill(_dragHwnd, _dragObstacles, visible, true, out target))
                {
                    HidePreview();
                    return;
                }
                // Alt를 놓으면 숨기되 타이머는 유지한다. 다시 누르면 같은 창을 다시 표시한다.
                ShowPreview(_dragHwnd, target);
            }
            catch (Exception ex)
            {
                StopDragPreview();
                Log.WriteAlways(L.T("창 미리보기 오류: ", "Window preview error: ") + ex.Message);
            }
        }

        private static void ShowPreview(IntPtr dragged, Native.RECT rect)
        {
            if (_previewVisible && SameRect(_previewRect, rect)) return;
            if (_previewHwnd == IntPtr.Zero)
            {
                if (!_previewClassRegistered)
                {
                    var wc = new Native.WNDCLASSEX
                    {
                        cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX)),
                        lpfnWndProc = _previewWndProc,
                        hInstance = Native.GetModuleHandle(null),
                        hbrBackground = Native.GetStockObject(Native.GRAY_BRUSH), // 공유 브러시라 해제하지 않는다.
                        lpszClassName = PreviewClassName
                    };
                    if (Native.RegisterClassEx(ref wc) == 0) return;
                    _previewClassRegistered = true;
                }
                _previewHwnd = Native.CreateWindowEx(
                    Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                    PreviewClassName, null, Native.WS_POPUP, 0, 0, 0, 0,
                    IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
                if (_previewHwnd == IntPtr.Zero) return;
                if (!Native.SetLayeredWindowAttributes(_previewHwnd, 0, 100, Native.LWA_ALPHA))
                {
                    Native.DestroyWindow(_previewHwnd);
                    _previewHwnd = IntPtr.Zero;
                    return;
                }
            }
            // hWndInsertAfter=대상 창: 바로 아래에 표시하며 포그라운드/대상 창은 건드리지 않는다.
            if (Native.SetWindowPos(_previewHwnd, dragged, rect.left, rect.top,
                rect.right - rect.left, rect.bottom - rect.top, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW))
            {
                _previewRect = rect;
                _previewVisible = true;
            }
        }

        private static void HidePreview()
        {
            if (_previewVisible && _previewHwnd != IntPtr.Zero)
                Native.ShowWindow(_previewHwnd, Native.SW_HIDE);
            _previewVisible = false;
        }

        internal static bool IsPreviewWindow(IntPtr hwnd)
        {
            return hwnd != IntPtr.Zero && hwnd == _previewHwnd;
        }

        private static void StopDragPreview()
        {
            try
            {
                if (_previewTimerOwner != IntPtr.Zero)
                    Native.KillTimer(_previewTimerOwner, new IntPtr(PreviewTimerId));
            }
            catch { } // 정리 실패도 WinEvent/타이머 콜백 밖으로 전파하지 않는다.
            finally
            {
                _previewTimerOwner = IntPtr.Zero;
                _dragHwnd = IntPtr.Zero;
                _dragObstacles = null;
                _dragAutoProcess = false;
                try { HidePreview(); }
                catch { }
            }
        }

        internal static void Shutdown()
        {
            try
            {
                StopDragPreview();
                if (_previewHwnd != IntPtr.Zero) Native.DestroyWindow(_previewHwnd);
                _previewHwnd = IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Log.WriteAlways(L.T("창 미리보기 종료 오류: ", "Window preview shutdown error: ") + ex.Message);
            }
        }

        private static IntPtr PreviewWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == Native.WM_NCHITTEST) return new IntPtr(Native.HTTRANSPARENT);
                if (msg == Native.WM_MOUSEACTIVATE) return new IntPtr(Native.MA_NOACTIVATE);
                return Native.DefWindowProc(hwnd, msg, wParam, lParam);
            }
            catch { return IntPtr.Zero; } // 미리보기 콜백의 예외가 본체 메시지 루프로 넘어가지 않게 한다.
        }

        public static bool IsAutoProcess(IntPtr hwnd)
        {
            if (Config.FillAutoProcesses.Count == 0) return false;
            string proc = App.ProcessNameOf(hwnd);
            if (string.IsNullOrEmpty(proc)) return false;
            for (int i = 0; i < Config.FillAutoProcesses.Count; i++)
            {
                if (string.Equals(proc, Config.FillAutoProcesses[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 계획에 명시된 두 핵심 예시(Case 1, Case 2)에 대해 계산 결과가 정확한지 검증한다.
        /// </summary>
        public static bool RunSelfTest()
        {
            // Case 1, 2 는 기능 요청 스크린샷 좌표 그대로다 (작업 영역 2000x810).
            var workArea = new Native.RECT { left = 0, top = 0, right = 2000, bottom = 810 };

            // Case 1: Muse 가 빈 틈 안에 있음 (좌 Antigravity, 우 사이드바) → 좌우상하 벽에 붙음
            var obs1 = new List<Native.RECT>
            {
                new Native.RECT { left = 55, top = 45, right = 766, bottom = 785 },
                new Native.RECT { left = 1388, top = 0, right = 2000, bottom = 810 }
            };
            var muse1 = new Native.RECT { left = 932, top = 8, right = 1252, bottom = 772 };
            Native.RECT res1;
            if (!Compute(workArea, obs1, muse1, out res1) ||
                res1.left != 766 || res1.top != 0 || res1.right != 1388 || res1.bottom != 810)
            {
                Log.Write("[자가진단 실패] Case 1 검증 불일치: " + string.Format("{0},{1} - {2},{3}", res1.left, res1.top, res1.right, res1.bottom));
                return false;
            }

            // Case 2: Muse 가 Antigravity 와 겹쳐 있음 (하단 440~1040 Antigravity, 우측 887~1920 사이드바)
            var obs2 = new List<Native.RECT>
            {
                new Native.RECT { left = 0, top = 440, right = 707, bottom = 810 },
                new Native.RECT { left = 887, top = 0, right = 2000, bottom = 810 }
            };
            var muse2 = new Native.RECT { left = 330, top = 80, right = 650, bottom = 610 };
            Native.RECT res2;
            if (!Compute(workArea, obs2, muse2, out res2) ||
                res2.left != 0 || res2.top != 0 || res2.right != 887 || res2.bottom != 440)
            {
                Log.Write("[자가진단 실패] Case 2 검증 불일치: " + string.Format("{0},{1} - {2},{3}", res2.left, res2.top, res2.right, res2.bottom));
                return false;
            }

            // Case 3: 창이 다른 창 위에 완전히 얹혀 있어 빈 공간과 겹침이 0인 경우 (가장 큰 빈 직사각형으로 이동)
            var obs3 = new List<Native.RECT>
            {
                new Native.RECT { left = 0, top = 0, right = 1000, bottom = 810 }
            };
            var muse3 = new Native.RECT { left = 100, top = 100, right = 500, bottom = 400 };
            Native.RECT res3;
            if (!Compute(workArea, obs3, muse3, out res3) ||
                res3.left != 1000 || res3.top != 0 || res3.right != 2000 || res3.bottom != 810)
            {
                Log.Write("[자가진단 실패] Case 3 검증 불일치: " + string.Format("{0},{1} - {2},{3}", res3.left, res3.top, res3.right, res3.bottom));
                return false;
            }

            Log.Write(L.T("[자가진단] 창 빈 공간 채우기 알고리즘 검증 통과 (Case 1, Case 2, Case 3 성공)",
                          "[selftest] Window fill algorithm self-test passed (Case 1, Case 2, Case 3 OK)"));
            return true;
        }
    }
}
