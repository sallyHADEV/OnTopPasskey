// 마우스 버튼 도움말 패널.
//
// 글로벌 단축키로 반투명 패널을 띄워 마우스 그림의 각 버튼 상자에 적어 둔 설명을 보여 준다.
// 트레이 메뉴의 "마우스 도움말 편집" 은 같은 그림 위에 입력 칸을 띄워 설명을 적게 한다.
//
// [경량화] 본체는 WinForms 를 쓰지 않는다. 이 파일만 WinForms/System.Drawing 을 쓰며,
//   바깥에 노출하는 메서드는 기본 형식만 받으므로 패널을 처음 띄우는 순간에야
//   두 어셈블리가 로드된다. 한 번도 안 쓰면 유휴 메모리는 그대로다.
//
// 설명 저장: %LOCALAPPDATA%\OnTopPasskey\mouse-help.txt (한 줄 = 버튼 하나, 0번부터)

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace OnTopPasskey
{
    internal static class MouseHelp
    {
        public const int HotkeyId = 1;
        // Ctrl+Alt+Shift+F12. 마우스 버튼에 할당해 쓰는 용도라 손으로 누르기 어려워도 되고,
        // 이 조합을 쓰는 프로그램은 사실상 없다.
        public const int HotkeyMods = 0x0001 | 0x0002 | 0x0004 | 0x4000;   // ALT | CONTROL | SHIFT | NOREPEAT
        public const int HotkeyVk = 0x7B;                                   // VK_F12
        public const string HotkeyText = "Ctrl+Alt+Shift+F12";

        // mouse-help.png(1108x1420) 위 설명 상자의 테두리 좌표. 인덱스 = 버튼 번호.
        private static readonly Rectangle[] Boxes = {
            new Rectangle(46, 343, 181, 66),   new Rectangle(46, 442, 204, 65),   new Rectangle(858, 412, 208, 62),
            new Rectangle(835, 309, 231, 70),  new Rectangle(46, 238, 204, 69),   new Rectangle(852, 22, 213, 60),
            new Rectangle(833, 208, 233, 69),  new Rectangle(63, 108, 221, 66),   new Rectangle(359, 75, 220, 63),
            new Rectangle(616, 85, 213, 63),   new Rectangle(401, 723, 241, 63),  new Rectangle(104, 746, 242, 66),
            new Rectangle(705, 731, 247, 68),  new Rectangle(851, 1169, 215, 59), new Rectangle(864, 942, 202, 65),
            new Rectangle(863, 1039, 203, 65), new Rectangle(850, 846, 216, 64),
        };

        private static Panel _open;

        private static string LabelsPath { get { return Path.Combine(Config.BaseDir, "mouse-help.txt"); } }

        /// <summary>단축키: 보기 패널을 열고 닫는다. 편집 중이면 무시.</summary>
        public static void Toggle()
        {
            if (_open != null) { if (!_open.Editing) _open.Close(); return; }
            Open(false);
        }

        /// <summary>트레이 메뉴: 편집 패널을 연다.</summary>
        public static void Edit()
        {
            if (_open != null) { if (_open.Editing) { _open.Activate(); return; } _open.Close(); }
            Open(true);
        }

        private static void Open(bool edit)
        {
            var p = new Panel(edit, LoadLabels());
            _open = p;
            p.FormClosed += delegate { if (_open == p) _open = null; Mem.Trim(); };
            if (!edit) { p.Show(); return; }   // 모달이 아닌 폼은 닫힐 때 스스로 Dispose 된다
            p.ShowDialog();
            p.Dispose();
        }

        private static string[] LoadLabels()
        {
            var labels = new string[Boxes.Length];
            try
            {
                if (File.Exists(LabelsPath))
                {
                    var lines = File.ReadAllLines(LabelsPath, Encoding.UTF8);
                    for (int i = 0; i < labels.Length && i < lines.Length; i++) labels[i] = lines[i];
                }
            }
            catch (Exception) { }
            for (int i = 0; i < labels.Length; i++) if (labels[i] == null) labels[i] = string.Empty;
            return labels;
        }

        private sealed class Panel : Form
        {
            private static readonly Color Bg = Color.FromArgb(32, 36, 40);

            public readonly bool Editing;
            private readonly string[] _labels;
            private readonly Image _img;
            private readonly float _scale;
            private readonly Font _font;
            private readonly TextBox[] _inputs;

            public Panel(bool edit, string[] labels)
            {
                Editing = edit;
                _labels = labels;
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mouse-help.png"))
                using (var tmp = new Bitmap(s))
                    _img = new Bitmap(tmp);

                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                _scale = Math.Min(1.2f, Math.Min(area.Width * 0.92f / _img.Width, area.Height * 0.94f / _img.Height));
                _font = new Font("Malgun Gothic", 19 * _scale, FontStyle.Bold, GraphicsUnit.Pixel);

                Text = "OnTopPasskey";
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                BackColor = Bg;
                DoubleBuffered = true;
                KeyPreview = true;
                ClientSize = new Size((int)(_img.Width * _scale), (int)(_img.Height * _scale));
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);

                if (!edit)
                {
                    Opacity = 0.95;
                    KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
                    MouseDown += delegate { Close(); };
                    Deactivate += delegate { Close(); };
                    Shown += delegate { Activate(); };
                    return;
                }

                _inputs = new TextBox[Boxes.Length];
                for (int i = 0; i < Boxes.Length; i++)
                {
                    var r = Inner(Boxes[i]);
                    var tb = new TextBox
                    {
                        Text = labels[i], Font = _font, BorderStyle = BorderStyle.None,
                        BackColor = Color.FromArgb(52, 58, 64), ForeColor = Color.White,
                        TextAlign = HorizontalAlignment.Center, TabIndex = i,
                    };
                    tb.SetBounds(r.X, r.Y + (r.Height - tb.PreferredHeight) / 2, r.Width, tb.PreferredHeight);
                    _inputs[i] = tb;
                    Controls.Add(tb);
                }

                // 아래쪽 빈 공간에 버튼과 안내. Enter = 저장, Esc = 취소.
                int y = (int)(1300 * _scale), h = (int)(52 * _scale), w = (int)(150 * _scale);
                var save = new Button { Text = L.T("저장", "Save"), Font = _font, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(40, 110, 200), TabIndex = 100 };
                var cancel = new Button { Text = L.T("취소", "Cancel"), Font = _font, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, TabIndex = 101 };
                save.SetBounds((int)(700 * _scale), y, w, h);
                cancel.SetBounds((int)(870 * _scale), y, w, h);
                save.Click += delegate { Save(); Close(); };
                cancel.Click += delegate { Close(); };
                Controls.Add(save);
                Controls.Add(cancel);
                AcceptButton = save;
                CancelButton = cancel;
            }

            private void Save()
            {
                var lines = new string[_inputs.Length];
                for (int i = 0; i < lines.Length; i++) lines[i] = _inputs[i].Text.Replace('\r', ' ').Replace('\n', ' ').Trim();
                try { File.WriteAllLines(LabelsPath, lines, new UTF8Encoding(false)); }
                catch (Exception ex)
                {
                    MessageBox.Show(this, L.T("저장 실패: ", "Could not save: ") + ex.Message, "OnTopPasskey",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            /// <summary>상자 테두리 안쪽 영역(화면 좌표).</summary>
            private Rectangle Inner(Rectangle b)
            {
                const int pad = 8;
                return new Rectangle((int)((b.X + pad) * _scale), (int)((b.Y + pad) * _scale),
                                     (int)((b.Width - pad * 2) * _scale), (int)((b.Height - pad * 2) * _scale));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawImage(_img, 0, 0, ClientSize.Width, ClientSize.Height);

                if (Editing)
                {
                    using (var hint = new Font(_font.FontFamily, 16 * _scale, GraphicsUnit.Pixel))
                        g.DrawString(L.T("각 상자에 버튼 설명을 입력하세요.  Tab 다음 칸 · Enter 저장 · Esc 취소\n보기 단축키: ",
                                         "Type a description in each box.  Tab next · Enter save · Esc cancel\nShow shortcut: ")
                                     + HotkeyText, hint, Brushes.Gainsboro, 40 * _scale, 1300 * _scale);
                    return;
                }

                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    for (int i = 0; i < Boxes.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(_labels[i])) continue;
                        var r = Inner(Boxes[i]);
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        // 한 줄에 안 들어가면 글자를 줄인다 (최소 60%). 그래도 넘치면 줄바꿈.
                        float w = g.MeasureString(_labels[i], _font).Width;
                        float ratio = w > 0f ? r.Width / w : 1f;
                        float size = _font.Size * Math.Max(0.6f, Math.Min(1f, ratio));
                        using (var f = new Font(_font.FontFamily, size, _font.Style, GraphicsUnit.Pixel))
                            g.DrawString(_labels[i], f, Brushes.White, r, sf);
                    }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _img.Dispose(); _font.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
