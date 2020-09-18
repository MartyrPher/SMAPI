namespace StardewModdingAPI.Mods.VirtualKeyboard
{
    class ModConfig
    {
        public Toggle vToggle { get; set; } = new Toggle(new Rect(36, 12, 64, 64), true);
        public VirtualButton[] buttons { get; set;} = new VirtualButton[] {
            new VirtualButton(SButton.Q, new Rect(190, 80, 90, 90), 0.5f),
            new VirtualButton(SButton.I, new Rect(290, 80, 90, 90), 0.5f),
            new VirtualButton(SButton.O, new Rect(390, 80, 90, 90), 0.5f),
            new VirtualButton(SButton.P, new Rect(490, 80, 90, 90), 0.5f)
        };
        public VirtualButton[] buttonsExtend { get; set; } = new VirtualButton[] {
            new VirtualButton(SButton.MouseRight, new Rect(190, 170, 162, 90), 0.5f, "RightMouse"),
            new VirtualButton(SButton.None, new Rect(360, 170, 92, 90), 0.5f, "Zoom", "zoom 1.0"),
            new VirtualButton(SButton.RightWindows, new Rect(460, 170, 162, 90), 0.5f, "Command"),
            new VirtualButton(SButton.RightControl, new Rect(630, 170, 162, 90), 0.5f, "Console")
        };
        internal class VirtualButton {
            public SButton key { get;set; }
            public Rect rectangle { get; set; }
            public float transparency { get; set; } = 0.5f;
            public string alias { get; set; } = null;
            public string command { get; set; } = null;
            public VirtualButton(SButton key, Rect rectangle, float transparency, string alias = null, string command = null)
            {
                this.key = key;
                this.rectangle = rectangle;
                this.transparency = transparency;
                this.alias = alias;
                this.command = command;
            }
        }
        internal class Toggle
        {
            public Rect rectangle { get; set; }
            public bool autoHidden { get; set; } = true;
            //public float scale;

            public Toggle(Rect rectangle, bool autoHidden)
            {
                this.rectangle = rectangle;
                this.autoHidden = autoHidden;
                //this.scale = scale;
            }
        }
        internal class Rect
        {
            public int X;
            public int Y;
            public int Width;
            public int Height;

            public Rect(int x, int y, int width, int height)
            {
                this.X = x;
                this.Y = y;
                this.Width = width;
                this.Height = height;
            }
        }
    }
}
