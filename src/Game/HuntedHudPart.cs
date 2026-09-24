using RWCustom;
using UnityEngine;

namespace Hunted.Game
{
    /// <summary>One line at the top of the screen saying where the Pursuer is and what it is doing.</summary>
    public class HuntedHudPart : HUD.HudPart
    {
        private readonly FLabel label;
        private int refresh;

        public HuntedHudPart(HUD.HUD hud) : base(hud)
        {
            label = new FLabel(Custom.GetFont(), "")
            {
                alignment = FLabelAlignment.Left,
                anchorX = 0f,
                anchorY = 1f,
                color = new Color(0.9f, 0.25f, 0.2f),
            };
            hud.fContainers[1].AddChild(label);
        }

        public override void Update()
        {
            base.Update();
            HuntedSession session = HuntedSession.Current;
            bool visible = session != null && session.OverlayVisible;
            label.isVisible = visible;
            if (!visible)
            {
                return;
            }
            if (--refresh <= 0)
            {
                refresh = 10;
                label.text = session.StatusLine();
            }
        }

        public override void Draw(float timeStacker)
        {
            base.Draw(timeStacker);
            Vector2 screen = hud.rainWorld.options.ScreenSize;
            label.x = 20.5f + hud.rainWorld.options.SafeScreenOffset.x;
            label.y = screen.y - 18.5f - hud.rainWorld.options.SafeScreenOffset.y;
        }

        public override void ClearSprites()
        {
            base.ClearSprites();
            label.RemoveFromContainer();
        }
    }
}
