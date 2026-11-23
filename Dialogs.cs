using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SafeSweep
{
    internal static class NativeWindowStyle
    {
        public const int WM_DPICHANGED = 0x02E0;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_CAPTION_COLOR = 35;
        public const int DWMWA_TEXT_COLOR = 36;

        public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static void EnableDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
                    return;
            }
            catch
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        public static void ApplyDarkTitleBar(IntPtr hwnd, Color captionColor, Color textColor)
        {
            try
            {
                int enabled = 1;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, sizeof(int));
                }

                int capRef = captionColor.R | (captionColor.G << 8) | (captionColor.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref capRef, sizeof(int));

                int txtRef = textColor.R | (textColor.G << 8) | (textColor.B << 16);
                DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref txtRef, sizeof(int));
            }
            catch
            {
            }
        }
    }

    internal sealed class FolderInspectorDialog : Form
    {
        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColSurface = Color.FromArgb(22, 27, 34);
        private static readonly Color ColBorder = Color.FromArgb(48, 58, 72);
        private static readonly Color ColText = Color.FromArgb(240, 246, 252);
        private static readonly Color ColBody = Color.FromArgb(204, 212, 220);
        private static readonly Color ColMuted = Color.FromArgb(139, 148, 158);
        private static readonly Color ColSafe = Color.FromArgb(52, 211, 153);
        private static readonly Color ColCaution = Color.FromArgb(251, 191, 36);

        private readonly CleanupCategory _cat;
        private readonly bool _isTurkish;
        private readonly ListView _lvFolders;
        private readonly ListView _lvFiles;

        public FolderInspectorDialog(CleanupCategory cat, bool isTurkish)
        {
            _cat = cat;
            _isTurkish = isTurkish;
            CultureInfo c = CleanupEngine.GetCulture(isTurkish);

            Text = isTurkish
                ? "Klasör Dökümü — " + cat.NameTr
                : "Folder Breakdown — " + cat.NameEn;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(650, 496);

            // Header title + badge
            Label lblTitle = new Label();
            lblTitle.UseMnemonic = false;
            lblTitle.Text = cat.GetName(isTurkish);
            lblTitle.Font = new Font("Segoe UI", 12.5f, FontStyle.Bold);
            lblTitle.ForeColor = ColText;
            lblTitle.Location = new Point(20, 16);
            lblTitle.AutoSize = true;
            Controls.Add(lblTitle);

            Label lblSummary = new Label();
            lblSummary.UseMnemonic = false;
            lblSummary.Text = string.Format(
                c,
                "{0}  ·  {1:N0} {2}",
                CleanupEngine.FormatBytes(cat.TotalBytes, isTurkish),
                cat.FileCount,
                isTurkish ? "dosya" : "files");
            lblSummary.Font = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            lblSummary.ForeColor = cat.Tier == SafetyTier.Safe ? ColSafe : ColCaution;
            lblSummary.Location = new Point(400, 18);
            lblSummary.Size = new Size(230, 22);
            lblSummary.TextAlign = ContentAlignment.MiddleRight;
            Controls.Add(lblSummary);

            Label lblDesc = new Label();
            lblDesc.UseMnemonic = false;
            lblDesc.Text = cat.GetDescription(isTurkish);
            lblDesc.ForeColor = ColMuted;
            lblDesc.Location = new Point(20, 44);
            lblDesc.Size = new Size(610, 22);
            Controls.Add(lblDesc);

            // Section 1: Scanned Folders
            Label lblFolders = new Label();
            lblFolders.Text = isTurkish ? "Taranan Klasörler:" : "Scanned Folders:";
            lblFolders.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold);
            lblFolders.ForeColor = ColBody;
            lblFolders.Location = new Point(20, 74);
            lblFolders.AutoSize = true;
            Controls.Add(lblFolders);

            _lvFolders = CreateDarkListView(new Point(20, 96), new Size(610, 128));
            _lvFolders.Columns.Add(isTurkish ? "Klasör Yolu" : "Folder Path", 420);
            _lvFolders.Columns.Add(isTurkish ? "Dosya" : "Files", 78, HorizontalAlignment.Right);
            _lvFolders.Columns.Add(isTurkish ? "Boyut" : "Size", 110, HorizontalAlignment.Right);

            foreach (TargetFolderStats st in cat.FolderBreakdowns)
            {
                ListViewItem lvi = new ListViewItem(st.Path);
                lvi.SubItems.Add(st.FileCount.ToString("N0", c));
                lvi.SubItems.Add(CleanupEngine.FormatBytes(st.Bytes, isTurkish));
                lvi.Tag = st.Path;
                _lvFolders.Items.Add(lvi);
            }
            _lvFolders.DoubleClick += delegate { OpenSelectedFolder(); };
            Controls.Add(_lvFolders);

            // Section 2: Largest Files
            Label lblFiles = new Label();
            lblFiles.Text = isTurkish
                ? "En Büyük Dosyalar (İlk 25 — Salt Okunur Önizleme):"
                : "Largest Files (Top 25 — Read-Only Preview):";
            lblFiles.Font = new Font("Segoe UI", 8.8f, FontStyle.Bold);
            lblFiles.ForeColor = ColBody;
            lblFiles.Location = new Point(20, 236);
            lblFiles.AutoSize = true;
            Controls.Add(lblFiles);

            _lvFiles = CreateDarkListView(new Point(20, 258), new Size(610, 178));
            _lvFiles.Columns.Add(isTurkish ? "Dosya Adı" : "File Name", 365);
            _lvFiles.Columns.Add(isTurkish ? "Son Değişiklik" : "Last Modified", 132);
            _lvFiles.Columns.Add(isTurkish ? "Boyut" : "Size", 111, HorizontalAlignment.Right);

            if (cat.IsRecycleBin)
            {
                ListViewItem lvi = new ListViewItem(isTurkish
                    ? "Geri Dönüşüm Kutusu içeriğini görmek için 'Klasörü Aç' butonuna tıklayın."
                    : "Click 'Open Folder' to view items inside the Windows Recycle Bin.");
                lvi.ForeColor = ColMuted;
                _lvFiles.Items.Add(lvi);
            }
            else if (cat.TopFiles.Count == 0)
            {
                ListViewItem lvi = new ListViewItem(isTurkish ? "Temizlenecek dosya bulunamadı." : "No eligible files found.");
                lvi.ForeColor = ColMuted;
                _lvFiles.Items.Add(lvi);
            }
            else
            {
                foreach (ScannedFileEntry f in cat.TopFiles)
                {
                    ListViewItem lvi = new ListViewItem(f.FileName);
                    string dt = isTurkish
                        ? f.LastWriteTime.ToString("dd.MM.yyyy HH:mm", c)
                        : f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", c);
                    lvi.SubItems.Add(dt);
                    lvi.SubItems.Add(CleanupEngine.FormatBytes(f.SizeBytes, isTurkish));
                    lvi.Tag = f.FullPath;
                    _lvFiles.Items.Add(lvi);
                }
            }
            _lvFiles.DoubleClick += delegate { OpenSelectedFileLocation(); };
            Controls.Add(_lvFiles);

            // Bottom buttons
            Button btnOpen = new Button();
            btnOpen.Text = isTurkish ? "Klasörü Gezginde Aç" : "Open Folder in Explorer";
            btnOpen.FlatStyle = FlatStyle.Flat;
            btnOpen.BackColor = ColSurface;
            btnOpen.ForeColor = ColSafe;
            btnOpen.FlatAppearance.BorderColor = ColBorder;
            btnOpen.Location = new Point(20, 448);
            btnOpen.Size = new Size(184, 34);
            btnOpen.Cursor = Cursors.Hand;
            btnOpen.Click += delegate { OpenSelectedFolder(); };
            Controls.Add(btnOpen);

            Button btnClose = new Button();
            btnClose.Text = isTurkish ? "Kapat" : "Close";
            btnClose.FlatStyle = FlatStyle.Flat;
            btnClose.BackColor = ColSurface;
            btnClose.ForeColor = ColText;
            btnClose.FlatAppearance.BorderColor = ColBorder;
            btnClose.Location = new Point(536, 448);
            btnClose.Size = new Size(94, 34);
            btnClose.Cursor = Cursors.Hand;
            btnClose.DialogResult = DialogResult.Cancel;
            Controls.Add(btnClose);

            CancelButton = btnClose;
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeWindowStyle.ApplyDarkTitleBar(Handle, ColBg, ColText);
        }

        internal static ListView CreateThemedDarkListView(Point loc, Size size, Color surfaceColor, Color bodyColor)
        {
            ListView lv = new ListView();
            lv.Location = loc;
            lv.Size = size;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.GridLines = false;
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.BorderStyle = BorderStyle.FixedSingle;
            lv.BackColor = surfaceColor;
            lv.ForeColor = bodyColor;
            lv.Font = new Font("Segoe UI", 8.6f);
            lv.OwnerDraw = true;

            Color hdrBg = Color.FromArgb(28, 35, 46);
            Color hdrBorder = Color.FromArgb(46, 56, 70);
            Color hdrText = Color.FromArgb(148, 163, 184);
            Color selBg = Color.FromArgb(30, 48, 62);

            lv.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush bg = new SolidBrush(hdrBg))
                using (Pen bd = new Pen(hdrBorder, 1f))
                using (SolidBrush fg = new SolidBrush(hdrText))
                using (Font f = new Font("Segoe UI", 8.2f, FontStyle.Bold))
                using (StringFormat sf = new StringFormat())
                {
                    e.Graphics.FillRectangle(bg, e.Bounds);
                    e.Graphics.DrawLine(bd, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Alignment = e.Header.TextAlign == HorizontalAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    Rectangle textRc = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, Math.Max(1, e.Bounds.Width - 16), e.Bounds.Height);
                    e.Graphics.DrawString(e.Header.Text, f, fg, textRc, sf);
                }
            };

            lv.DrawItem += delegate(object sender, DrawListViewItemEventArgs e)
            {
                // In View.Details mode, DrawSubItem paints every cell cleanly.
            };

            lv.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                Rectangle cellRc = e.ColumnIndex == 0
                    ? new Rectangle(e.Bounds.X, e.Bounds.Y, e.Header.Width, e.Bounds.Height)
                    : e.Bounds;
                Color rowBg = e.Item.Selected ? selBg : surfaceColor;
                Color fgCol = e.Item.ForeColor != Color.Empty ? e.Item.ForeColor : bodyColor;
                using (SolidBrush bg = new SolidBrush(rowBg))
                using (SolidBrush fg = new SolidBrush(fgCol))
                using (StringFormat sf = new StringFormat())
                {
                    e.Graphics.FillRectangle(bg, cellRc);
                    sf.LineAlignment = StringAlignment.Center;
                    sf.Alignment = e.Header.TextAlign == HorizontalAlignment.Right ? StringAlignment.Far : StringAlignment.Near;
                    sf.FormatFlags = StringFormatFlags.NoWrap;
                    sf.Trimming = StringTrimming.EllipsisCharacter;
                    Rectangle textRc = new Rectangle(cellRc.X + 8, cellRc.Y, Math.Max(1, cellRc.Width - 16), cellRc.Height);
                    e.Graphics.DrawString(e.SubItem.Text, lv.Font, fg, textRc, sf);
                }
            };

            return lv;
        }

        private ListView CreateDarkListView(Point loc, Size size)
        {
            return CreateThemedDarkListView(loc, size, ColSurface, ColBody);
        }

        private void OpenSelectedFolder()
        {
            try
            {
                if (_cat.IsRecycleBin)
                {
                    Process.Start("explorer.exe", "shell:RecycleBinFolder");
                    return;
                }

                string pathToOpen = null;
                if (_lvFolders.SelectedItems.Count > 0)
                    pathToOpen = _lvFolders.SelectedItems[0].Tag as string;
                else if (_cat.FolderBreakdowns.Count > 0)
                    pathToOpen = _cat.FolderBreakdowns[0].Path;

                if (!string.IsNullOrEmpty(pathToOpen) && Directory.Exists(pathToOpen))
                    Process.Start("explorer.exe", "\"" + pathToOpen + "\"");
            }
            catch
            {
            }
        }

        private void OpenSelectedFileLocation()
        {
            try
            {
                if (_lvFiles.SelectedItems.Count > 0)
                {
                    string filePath = _lvFiles.SelectedItems[0].Tag as string;
                    if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                        Process.Start("explorer.exe", "/select,\"" + filePath + "\"");
                }
            }
            catch
            {
            }
        }
    }

    internal sealed class ConfirmCleanDialog : Form
    {
        private static readonly Color ColBg = Color.FromArgb(13, 17, 23);
        private static readonly Color ColSurface = Color.FromArgb(22, 27, 34);
        private static readonly Color ColBorder = Color.FromArgb(48, 58, 72);
        private static readonly Color ColText = Color.FromArgb(240, 246, 252);
        private static readonly Color ColBody = Color.FromArgb(204, 212, 220);
        private static readonly Color ColMuted = Color.FromArgb(139, 148, 158);
        private static readonly Color ColSafe = Color.FromArgb(52, 211, 153);
        private static readonly Color ColCaution = Color.FromArgb(251, 191, 36);

        public ConfirmCleanDialog(List<CleanupCategory> selected, bool isTurkish)
        {
            CultureInfo c = CleanupEngine.GetCulture(isTurkish);
            long totalBytes = 0;
            long totalFiles = 0;
            bool hasCaution = false;
            bool hasRunning = false;

            foreach (CleanupCategory cat in selected)
            {
                totalBytes += cat.TotalBytes;
                totalFiles += cat.FileCount;
                if (cat.Tier == SafetyTier.Caution) hasCaution = true;
                if (cat.IsAppRunning) hasRunning = true;
            }

            Text = isTurkish ? "Temizliği Onayla" : "Confirm Cleanup";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = ColBg;
            ForeColor = ColText;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular);

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(470, 390);

            Label lblTitle = new Label();
            lblTitle.Text = isTurkish
                ? string.Format(c, "{0} ({1:N0} dosya) kalıcı olarak temizlenecek", CleanupEngine.FormatBytes(totalBytes, true), totalFiles)
                : string.Format(c, "Permanently clean {0} ({1:N0} files)", CleanupEngine.FormatBytes(totalBytes, false), totalFiles);
            lblTitle.Font = new Font("Segoe UI", 10.8f, FontStyle.Bold);
            lblTitle.ForeColor = hasCaution ? ColCaution : ColSafe;
            lblTitle.Location = new Point(20, 16);
            lblTitle.Size = new Size(430, 24);
            Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.Text = isTurkish
                ? "Seçtiğiniz kalemler aşağıdadır. Aktif/kilitli dosyalar ve sembolik bağlantılar otomatik korunur."
                : "Selected items are listed below. Locked/in-use files and symlinks are automatically protected.";
            lblSub.ForeColor = ColMuted;
            lblSub.Location = new Point(20, 44);
            lblSub.Size = new Size(430, 34);
            Controls.Add(lblSub);

            ListView lv = FolderInspectorDialog.CreateThemedDarkListView(new Point(20, 84), new Size(430, 186), ColSurface, ColBody);
            lv.Columns.Add(isTurkish ? "Kategori" : "Category", 268);
            lv.Columns.Add(isTurkish ? "Dosya" : "Files", 66, HorizontalAlignment.Right);
            lv.Columns.Add(isTurkish ? "Boyut" : "Size", 94, HorizontalAlignment.Right);

            foreach (CleanupCategory cat in selected)
            {
                string prefix = cat.Tier == SafetyTier.Caution ? "[!] " : "";
                ListViewItem lvi = new ListViewItem(prefix + cat.GetName(isTurkish));
                lvi.SubItems.Add(cat.FileCount.ToString("N0", c));
                lvi.SubItems.Add(CleanupEngine.FormatBytes(cat.TotalBytes, isTurkish));
                if (cat.Tier == SafetyTier.Caution)
                    lvi.ForeColor = ColCaution;
                lv.Items.Add(lvi);
            }
            Controls.Add(lv);

            string warnText = "";
            if (hasCaution)
            {
                warnText += isTurkish
                    ? "• Dikkat [!] işaretli öğeler seçili (örn. Geri Dönüşüm Kutusu / Shader önbelleği).\r\n"
                    : "• Caution [!] items are selected (e.g. Recycle Bin / Shader cache).\r\n";
            }
            if (hasRunning)
            {
                warnText += isTurkish
                    ? "• Açık olan tarayıcı/uygulamaların kilitli önbellek dosyaları güvenle atlanacaktır."
                    : "• Some open browser/app cache files are locked and will be safely skipped.";
            }
            if (string.IsNullOrEmpty(warnText))
            {
                warnText = isTurkish
                    ? "Yalnızca güvenli (SAFE) önbellek ve geçici dosyalar seçildi. Kişisel dosyalarınız ve şifreleriniz etkilenmez."
                    : "Only SAFE caches & temporary files are selected. Personal files, logins & cookies are untouched.";
            }

            Label lblWarn = new Label();
            lblWarn.UseMnemonic = false;
            lblWarn.Text = warnText.Trim();
            lblWarn.ForeColor = (hasCaution || hasRunning) ? ColCaution : ColMuted;
            lblWarn.Font = new Font("Segoe UI", 8.3f);
            lblWarn.Location = new Point(20, 280);
            lblWarn.Size = new Size(430, 48);
            Controls.Add(lblWarn);

            Button btnConfirm = new Button();
            btnConfirm.UseMnemonic = false;
            btnConfirm.Text = isTurkish ? "Onayla ve Temizle" : "Confirm & Clean";
            btnConfirm.FlatStyle = FlatStyle.Flat;
            btnConfirm.BackColor = Color.FromArgb(18, 66, 48);
            btnConfirm.ForeColor = Color.FromArgb(110, 231, 183);
            btnConfirm.FlatAppearance.BorderColor = ColSafe;
            btnConfirm.Location = new Point(216, 338);
            btnConfirm.Size = new Size(134, 34);
            btnConfirm.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            btnConfirm.Cursor = Cursors.Hand;
            btnConfirm.DialogResult = DialogResult.OK;
            Controls.Add(btnConfirm);

            Button btnCancel = new Button();
            btnCancel.Text = isTurkish ? "İptal" : "Cancel";
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.BackColor = ColSurface;
            btnCancel.ForeColor = ColText;
            btnCancel.FlatAppearance.BorderColor = ColBorder;
            btnCancel.Location = new Point(358, 338);
            btnCancel.Size = new Size(92, 34);
            btnCancel.Cursor = Cursors.Hand;
            btnCancel.DialogResult = DialogResult.Cancel;
            Controls.Add(btnCancel);

            AcceptButton = btnConfirm;
            CancelButton = btnCancel;

            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeWindowStyle.ApplyDarkTitleBar(Handle, ColBg, ColText);
        }
    }
}
