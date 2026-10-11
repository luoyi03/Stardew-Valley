using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace InGameGuide
{
    public class ScrollBar
    {
        public Rectangle Track;
        public int Content;
        public int View;
        private int _pos;

        public bool Dragging;
        private int _dragOffset;

        public int Pos
        {
            get => _pos;
            set => _pos = Math.Max(0, Math.Min(Max, value));
        }

        public int Max => Math.Max(0, Content - View);

        public Rectangle Thumb
        {
            get
            {
                if (Max <= 0 || Track.Height <= 0)
                    return Rectangle.Empty;
                int h = Math.Max(24, (int)(View * Track.Height / (double)Content));
                double ratio = Pos / (double)Max;
                int y = Track.Y + (int)((Track.Height - h) * ratio);
                return new Rectangle(Track.X, y, Track.Width, h);
            }
        }

        public void ScrollBy(int delta) => Pos += delta;

        public bool TryGrab(int x, int y)
        {
            var thumb = Thumb;
            if (thumb != Rectangle.Empty && thumb.Contains(x, y))
            {
                Dragging = true;
                _dragOffset = y - thumb.Y;
                return true;
            }
            if (Track.Contains(x, y))
            {
                Dragging = true;
                _dragOffset = Thumb.Height / 2;
                ApplyDrag(y);
                return true;
            }
            return false;
        }

        public void ApplyDrag(int y)
        {
            if (Max <= 0 || Track.Height <= 0)
                return;
            int th = Math.Max(24, (int)(View * Track.Height / (double)Content));
            double ratio = (y - _dragOffset - Track.Y) / (double)Math.Max(1, Track.Height - th);
            Pos = (int)Math.Round(Max * ratio);
        }

        public void Release() => Dragging = false;

        public void Draw(SpriteBatch b)
        {
            if (Max <= 0)
                return;
            Ui.Fill(b, Track, Ui.PanelBg2);
            var thumb = Thumb;
            if (thumb != Rectangle.Empty)
                Ui.Fill(b, thumb, Dragging ? Ui.Gold : Ui.HiLine);
        }
    }
}
