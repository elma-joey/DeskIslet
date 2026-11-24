using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using F = System.Windows.Forms;

namespace Deskfold
{
    // The frozen desktop and crop coordinates are both in physical screen pixels.
    internal sealed class SnipOverlay : F.Form
    {
        readonly Bitmap desktop;
        Point start, end;
        bool selecting;
        static SnipOverlay active;

        public static void Begin()
        {
            if (active != null) { active.Activate(); return; }
            Rectangle bounds = F.SystemInformation.VirtualScreen;
            var image = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            try {
                using (var graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                active = new SnipOverlay(image, bounds);
                active.Show(); active.Activate();
            } catch { if (active != null) active.Dispose(); active = null; image.Dispose(); throw; }
        }

        SnipOverlay(Bitmap image, Rectangle bounds)
        {
            desktop = image;
            FormBorderStyle = F.FormBorderStyle.None; StartPosition = F.FormStartPosition.Manual;
            AutoScaleMode = F.AutoScaleMode.None; Bounds = bounds; TopMost = true; ShowInTaskbar = false;
            DoubleBuffered = true; KeyPreview = true; Cursor = F.Cursors.Cross; Text = "桌屿 · 区域截图";
            Shown += delegate { Bounds = bounds; };
            KeyDown += (s,e) => { if (e.KeyCode == F.Keys.Escape) Close(); };
            FormClosed += delegate { active = null; desktop.Dispose(); };
            MouseDown += (s,e) => {
                if (e.Button == F.MouseButtons.Right) { Close(); return; }
                if (e.Button == F.MouseButtons.Left) { Capture = true; selecting = true; start = end = e.Location; Invalidate(); }
            };
            MouseMove += (s,e) => { if (selecting) { end = e.Location; Invalidate(); } };
            MouseUp += (s,e) => {
                if (!selecting || e.Button != F.MouseButtons.Left) return;
                end = e.Location; selecting = false; Capture = false;
                Rectangle crop = Selection();
                if (crop.Width < 2 || crop.Height < 2) { Invalidate(); return; }
                var part = desktop.Clone(crop, PixelFormat.Format32bppRgb);
                Point position = PointToScreen(crop.Location);
                Close();
                new SnipPreview(part, position, false).Show();
            };
        }

        Rectangle Selection()
        {
            return Rectangle.Intersect(new Rectangle(Point.Empty, desktop.Size),
                Rectangle.FromLTRB(Math.Min(start.X,end.X), Math.Min(start.Y,end.Y), Math.Max(start.X,end.X), Math.Max(start.Y,end.Y)));
        }
        protected override void OnPaint(F.PaintEventArgs e)
        {
            e.Graphics.DrawImageUnscaled(desktop, 0, 0);
            using (var shade = new SolidBrush(Color.FromArgb(105, Color.Black))) e.Graphics.FillRectangle(shade, ClientRectangle);
            var crop = Selection();
            if (selecting && crop.Width > 0 && crop.Height > 0) {
                e.Graphics.DrawImage(desktop, crop, crop, GraphicsUnit.Pixel);
                e.Graphics.DrawRectangle(Pens.DeepSkyBlue, crop.X, crop.Y, crop.Width-1, crop.Height-1);
            } else {
                var area = F.Screen.FromPoint(F.Cursor.Position).Bounds;
                F.TextRenderer.DrawText(e.Graphics, "拖动框选截图   Esc 取消", Font,
                    PointToClient(new Point(area.Left + 24,area.Top + 24)), Color.White, Color.FromArgb(35,38,42));
            }
        }
    }

    internal sealed class SnipPreview : F.Form
    {
        readonly Bitmap image;
        readonly F.ToolStrip actions = new F.ToolStrip();
        Point dragOffset;
        bool dragging;
        public static void PinClipboard()
        {
            using (var img = F.Clipboard.GetImage()) {
                if (img == null) { F.MessageBox.Show("请先复制一张图片。", "桌屿 · 贴图"); return; }
                new SnipPreview(new Bitmap(img), F.Cursor.Position, true).Show();
            }
        }
        public SnipPreview(Bitmap image, Point location, bool pinned)
        {
            this.image = image;
            Text = "桌屿 · 截图"; FormBorderStyle = F.FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = F.FormStartPosition.Manual; TopMost = true; KeyPreview = true; AutoScaleMode = F.AutoScaleMode.None;
            BackColor = Color.FromArgb(38,40,45);
            Rectangle work = F.Screen.FromPoint(location).WorkingArea;
            double ratio = Math.Min(1, Math.Min((double)work.Width / image.Width,(double)(work.Height-42) / image.Height));
            var size = new Size(Math.Max(1,(int)(image.Width*ratio)), Math.Max(1,(int)(image.Height*ratio)));
            var picture = new F.PictureBox { Size = size, SizeMode = F.PictureBoxSizeMode.StretchImage, Image = image };
            Controls.Add(picture);
            actions.GripStyle = F.ToolStripGripStyle.Hidden; actions.Dock = F.DockStyle.None;
            actions.Location = new Point(0,size.Height); actions.AutoSize = true;
            actions.Items.Add("复制", null, (s,e) => Copy());
            actions.Items.Add("保存", null, (s,e) => SaveImage());
            actions.Items.Add("贴图", null, (s,e) => Pin(size));
            actions.Items.Add("关闭", null, (s,e) => Close()); Controls.Add(actions);
            ClientSize = new Size(Math.Max(actions.PreferredSize.Width,size.Width),size.Height+actions.PreferredSize.Height);
            if (pinned) Pin(size);
            Location = new Point(Math.Max(work.Left,Math.Min(location.X, work.Right-Width)),
                                 Math.Max(work.Top,Math.Min(location.Y,work.Bottom-Height)));
            var menu = new F.ContextMenuStrip();
            menu.Items.Add("复制图片",null,(s,e) => Copy()); menu.Items.Add("另存为 PNG…",null,(s,e) => SaveImage());
            menu.Items.Add("关闭贴图",null,(s,e) => Close()); picture.ContextMenuStrip = menu;
            picture.MouseDown += (s,e) => { if (e.Button == F.MouseButtons.Left) { dragging=true; dragOffset=e.Location; picture.Capture=true; } };
            picture.MouseMove += (s,e) => { if (dragging) Location = Point.Subtract(F.Cursor.Position,new Size(dragOffset)); };
            picture.MouseUp += delegate { dragging=false; picture.Capture=false; };
            picture.DoubleClick += delegate { Copy(); };
            KeyDown += (s,e) => {
                if(e.KeyCode == F.Keys.Escape) Close();
                else if(e.Control && e.KeyCode == F.Keys.C) Copy();
                else if(e.Control && e.KeyCode == F.Keys.S) SaveImage();
            };
            FormClosed += delegate { picture.Image=null; image.Dispose(); menu.Dispose(); };
        }
        void Pin(Size size) { actions.Visible=false; ClientSize=size; Text="桌屿 · 贴图"; }
        void Copy()
        {
            try { F.Clipboard.SetImage(image); if(actions.Visible) Close(); }
            catch (ExternalException ex) { F.MessageBox.Show(ex.Message,"复制失败"); }
        }
        void SaveImage()
        {
            using(var dialog = new F.SaveFileDialog { Filter="PNG 图片|*.png", FileName="截图 "+DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss")+".png",
                InitialDirectory=Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) }) {
                if(dialog.ShowDialog(this) != F.DialogResult.OK) return;
                try { image.Save(dialog.FileName,ImageFormat.Png); if(actions.Visible) Close(); }
                catch(Exception ex) { F.MessageBox.Show(ex.Message,"保存截图失败"); }
            }
        }
    }
}
