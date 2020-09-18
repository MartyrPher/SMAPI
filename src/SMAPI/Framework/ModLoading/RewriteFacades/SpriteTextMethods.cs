#if SMAPI_FOR_MOBILE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework.Graphics;
using StardewValley.BellsAndWhistles;

namespace StardewModdingAPI.Framework.ModLoading.RewriteFacades
{
    public class SpriteTextMethods : SpriteText
    {
        public new static int getWidthOfString(string s, int widthConstraint = 999999)
        {
            return SpriteText.getWidthOfString(s);
        }

        public new static void drawStringHorizontallyCenteredAt(SpriteBatch b, string s, int x, int y, int characterPosition = 999999, int width = -1, int height = 999999, float alpha = -1f, float layerDepth = 0.088f, bool junimoText = false, int color = -1, int maxWdith = 99999)
        {
            SpriteText.drawString(b, s, x - SpriteText.getWidthOfString(s) / 2, y, characterPosition, width, height, alpha, layerDepth, junimoText, -1, "", color);
        }

        public static void drawStringWithScrollBackground(SpriteBatch b, string s, int x, int y, string placeHolderWidthText = "", float alpha = 1f, int color = -1, SpriteText.ScrollTextAlignment scroll_text_alignment = SpriteText.ScrollTextAlignment.Left)
        {
            SpriteText.drawString(b, s, x, y, 999999, -1, 999999, alpha, 0.088f, false, 0, placeHolderWidthText, color, scroll_text_alignment);
        }

        public static void drawStringWithScrollBackground(SpriteBatch b, string s, int x, int y, string placeHolderWidthText, float alpha, int color)
        {
            SpriteText.drawStringWithScrollBackground(b, s, x, y, placeHolderWidthText, alpha, color, 0.088f);
        }
    }
}
#endif
