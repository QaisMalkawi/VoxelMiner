namespace VoxelMiner.Core
{
    public static class EasingUtilities
    {
        const double PI = Math.PI;
        const double C1 = 1.70158, C2 = C1 * 1.525, C3 = C1 + 1, C4 = (2 * Math.PI) / 3, C5 = (2 * Math.PI) / 4.5;
        static double BOut(double x)
        {
            const double n1 = 7.5625, d1 = 2.75;
            if (x < 1 / d1) return n1 * x * x;
            if (x < 2 / d1) { x -= 1.5 / d1; return n1 * x * x + .75; }
            if (x < 2.5 / d1) { x -= 2.25 / d1; return n1 * x * x + .9375; }
            x -= 2.625 / d1; return n1 * x * x + .984375;
        }
        // Double
        public static double Linear(this double x) => x;
        public static double EaseInSine(this double x) => 1 - Math.Cos((x * PI) / 2);
        public static double EaseOutSine(this double x) => Math.Sin((x * PI) / 2);
        public static double EaseInOutSine(this double x) => -(Math.Cos(PI * x) - 1) / 2;
        public static double EaseInQuad(this double x) => x * x;
        public static double EaseOutQuad(this double x) => 1 - (1 - x) * (1 - x);
        public static double EaseInOutQuad(this double x) => x < .5 ? 2 * x * x : 1 - Math.Pow(-2 * x + 2, 2) / 2;
        public static double EaseInCubic(this double x) => x * x * x;
        public static double EaseOutCubic(this double x) => 1 - Math.Pow(1 - x, 3);
        public static double EaseInOutCubic(this double x) => x < .5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
        public static double EaseInQuart(this double x) => x * x * x * x;
        public static double EaseOutQuart(this double x) => 1 - Math.Pow(1 - x, 4);
        public static double EaseInOutQuart(this double x) => x < .5 ? 8 * Math.Pow(x, 4) : 1 - Math.Pow(-2 * x + 2, 4) / 2;
        public static double EaseInQuint(this double x) => Math.Pow(x, 5);
        public static double EaseOutQuint(this double x) => 1 - Math.Pow(1 - x, 5);
        public static double EaseInOutQuint(this double x) => x < .5 ? 16 * Math.Pow(x, 5) : 1 - Math.Pow(-2 * x + 2, 5) / 2;
        public static double EaseInExpo(this double x) => x == 0 ? 0 : Math.Pow(2, 10 * x - 10);
        public static double EaseOutExpo(this double x) => x == 1 ? 1 : 1 - Math.Pow(2, -10 * x);
        public static double EaseInOutExpo(this double x) => x == 0 ? 0 : x == 1 ? 1 : x < .5 ? Math.Pow(2, 20 * x - 10) / 2 : (2 - Math.Pow(2, -20 * x + 10)) / 2;
        public static double EaseInCirc(this double x) => 1 - Math.Sqrt(1 - x * x);
        public static double EaseOutCirc(this double x) => Math.Sqrt(1 - Math.Pow(x - 1, 2));
        public static double EaseInOutCirc(this double x) => x < .5 ? (1 - Math.Sqrt(1 - Math.Pow(2 * x, 2))) / 2 : (Math.Sqrt(1 - Math.Pow(-2 * x + 2, 2)) + 1) / 2;
        public static double EaseInBack(this double x) => C3 * x * x * x - C1 * x * x;
        public static double EaseOutBack(this double x) { var t = x - 1; return 1 + C3 * t * t * t + C1 * t * t; }
        public static double EaseInOutBack(this double x) => x < .5 ? (Math.Pow(2 * x, 2) * ((C2 + 1) * 2 * x - C2)) / 2 : (Math.Pow(2 * x - 2, 2) * ((C2 + 1) * (2 * x - 2) + C2) + 2) / 2;
        public static double EaseInElastic(this double x) => x == 0 ? 0 : x == 1 ? 1 : -Math.Pow(2, 10 * x - 10) * Math.Sin((x * 10 - 10.75) * C4);
        public static double EaseOutElastic(this double x) => x == 0 ? 0 : x == 1 ? 1 : Math.Pow(2, -10 * x) * Math.Sin((x * 10 - .75) * C4) + 1;
        public static double EaseInOutElastic(this double x) => x == 0 ? 0 : x == 1 ? 1 : x < .5 ? -(Math.Pow(2, 20 * x - 10) * Math.Sin((20 * x - 11.125) * C5)) / 2 : (Math.Pow(2, -20 * x + 10) * Math.Sin((20 * x - 11.125) * C5)) / 2 + 1;
        public static double EaseOutBounce(this double x) => BOut(x);
        public static double EaseInBounce(this double x) => 1 - BOut(1 - x);
        public static double EaseInOutBounce(this double x) => x < .5 ? (1 - BOut(1 - 2 * x)) / 2 : (1 + BOut(2 * x - 1)) / 2;
        // float wrappers
        public static float Linear(this float x) => (float)Linear((double)x);
        public static float EaseInSine(this float x) => (float)EaseInSine((double)x);
        public static float EaseOutSine(this float x) => (float)EaseOutSine((double)x);
        public static float EaseInOutSine(this float x) => (float)EaseInOutSine((double)x);
        public static float EaseInQuad(this float x) => (float)EaseInQuad((double)x);
        public static float EaseOutQuad(this float x) => (float)EaseOutQuad((double)x);
        public static float EaseInOutQuad(this float x) => (float)EaseInOutQuad((double)x);
        public static float EaseInCubic(this float x) => (float)EaseInCubic((double)x);
        public static float EaseOutCubic(this float x) => (float)EaseOutCubic((double)x);
        public static float EaseInOutCubic(this float x) => (float)EaseInOutCubic((double)x);
        public static float EaseInQuart(this float x) => (float)EaseInQuart((double)x);
        public static float EaseOutQuart(this float x) => (float)EaseOutQuart((double)x);
        public static float EaseInOutQuart(this float x) => (float)EaseInOutQuart((double)x);
        public static float EaseInQuint(this float x) => (float)EaseInQuint((double)x);
        public static float EaseOutQuint(this float x) => (float)EaseOutQuint((double)x);
        public static float EaseInOutQuint(this float x) => (float)EaseInOutQuint((double)x);
        public static float EaseInExpo(this float x) => (float)EaseInExpo((double)x);
        public static float EaseOutExpo(this float x) => (float)EaseOutExpo((double)x);
        public static float EaseInOutExpo(this float x) => (float)EaseInOutExpo((double)x);
        public static float EaseInCirc(this float x) => (float)EaseInCirc((double)x);
        public static float EaseOutCirc(this float x) => (float)EaseOutCirc((double)x);
        public static float EaseInOutCirc(this float x) => (float)EaseInOutCirc((double)x);
        public static float EaseInBack(this float x) => (float)EaseInBack((double)x);
        public static float EaseOutBack(this float x) => (float)EaseOutBack((double)x);
        public static float EaseInOutBack(this float x) => (float)EaseInOutBack((double)x);
        public static float EaseInElastic(this float x) => (float)EaseInElastic((double)x);
        public static float EaseOutElastic(this float x) => (float)EaseOutElastic((double)x);
        public static float EaseInOutElastic(this float x) => (float)EaseInOutElastic((double)x);
        public static float EaseInBounce(this float x) => (float)EaseInBounce((double)x);
        public static float EaseOutBounce(this float x) => (float)EaseOutBounce((double)x);
        public static float EaseInOutBounce(this float x) => (float)EaseInOutBounce((double)x);
    }
}
