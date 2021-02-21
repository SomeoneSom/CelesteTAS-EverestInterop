using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Monocle;
using TAS.Input;
using GameInput = Celeste.Input;

namespace TAS {
    public enum AnalogueMode {
        Ignore,
        Square,
        Precise,
    }

    public struct Vector2Short {
        public short X, Y;

        public Vector2Short(short x = 0, short y = 0) {
            X = x;
            Y = y;
        }
    }

    public static class AnalogHelper {
        private const double DeadZone = 0.239532471;
        private const double DcMult = (1 - DeadZone) * (1 - DeadZone);
        private const float AmpLowerbound = (float) (0.25 * DcMult);
        private const short Lowerbound = 7849;

        private static readonly Regex Fractional = new Regex(@"\d+\.(\d*)", RegexOptions.Compiled);
        private static AnalogueMode analogMode = AnalogueMode.Ignore;
        private static short upperbound = 32767;
        private static Vector2 retDirection;
        private static Vector2Short retDirectionShort;

        public static void AnalogModeChange(AnalogueMode mode, float? upperLimit = null) {
            analogMode = mode;

            if (upperLimit.HasValue) {
                upperbound = (short) Calc.Clamp(upperLimit.Value * 32767, Lowerbound, 32767);
                ;
            }
        }

        public static void ComputeAngleVector2(InputFrame input) {
            float precision;
            if (input.Angle == 0) {
                precision = 1E-6f;
            } else {
                int digits = 0;
                Match match = Fractional.Match(input.Angle.ToString(CultureInfo.InvariantCulture));
                if (match.Success) {
                    digits = match.Value.Length;
                }

                precision = float.Parse($"0.5E-{digits + 2}");
            }

            input.AngleVector2 = ComputeFeather(input.GetX(), input.GetY(), precision);
            input.AngleVector2Short = retDirectionShort;
        }

        private static Vector2Short ComputePrecise(Vector2 direction, float precision) {
            // it should hold that direction has x>0, y>0, x>y
            // we look for the least y/x difference
            if (direction.Y < 1e-10) {
                return new Vector2Short(upperbound, 0);
            }

            double approx = direction.Y / direction.X;
            double multip = direction.X / direction.Y;
            double upperl = (double) upperbound / 32767;
            double leastError = approx;
            short retX = upperbound, retY = 0; // y/x=0, so error is approx
            short y = Lowerbound;
            while (true) {
                double ys = (double) y / 32767 - DeadZone;
                double xx = Math.Min(DeadZone + multip * ys, upperl);
                short x = (short) Math.Floor(xx * 32767);
                double xs = (double) x / 32767 - DeadZone;
                double error = Math.Abs(ys / xs - approx);
                if (xs * xs + ys * ys >= AmpLowerbound && (error < leastError || error <= precision)) {
                    leastError = error;
                    retX = x;
                    retY = y;
                }

                if (x < upperbound) {
                    ++x;
                    xs = (double) x / 32767 - DeadZone;
                    error = Math.Abs(ys / xs - approx);
                    if (xs * xs + ys * ys >= AmpLowerbound && (error < leastError || error <= precision)) {
                        leastError = error;
                        retX = x;
                        retY = y;
                    }
                }

                if (xx >= upperl) {
                    break;
                }

                ++y;
            }

            return new Vector2Short(retX, retY);
        }

        private static Vector2 ComputeFeather(float x, float y, float precision) {
            if (x < 0) {
                Vector2 feather = ComputeFeather(-x, y, precision);
                retDirectionShort.X = (short) -retDirectionShort.X;
                return retDirection = new Vector2(-feather.X, feather.Y);
            }

            if (y < 0) {
                Vector2 feather = ComputeFeather(x, -y, precision);
                retDirectionShort.Y = (short) -retDirectionShort.Y;
                return retDirection = new Vector2(feather.X, -feather.Y);
            }

            if (x < y) {
                Vector2 feather = ComputeFeather(y, x, precision);
                retDirectionShort = new Vector2Short(retDirectionShort.Y, retDirectionShort.X);
                return retDirection = new Vector2(feather.Y, feather.X);
            }

            // assure positive and x>=y
            short shortX, shortY;
            switch (analogMode) {
                case AnalogueMode.Ignore:
                    retDirectionShort = ComputePrecise(new Vector2(x, y), precision);
                    return retDirection = new Vector2(x, y);
                case AnalogueMode.Square:
                    float divisor = Math.Max(Math.Abs(x), Math.Abs(y));
                    x /= divisor;
                    y /= divisor;
                    shortX = (short) Math.Round((x * (1.0 - DeadZone) + DeadZone) * 32767);
                    shortY = (short) Math.Round((y * (1.0 - DeadZone) + DeadZone) * 32767);
                    break;
                case AnalogueMode.Precise:
                    Vector2Short result = ComputePrecise(new Vector2(x, y), precision);
                    shortX = result.X;
                    shortY = result.Y;
                    break;
                default:
                    throw new Exception("what the fuck");
            }

            retDirectionShort = new Vector2Short(shortX, shortY);
            x = (float) (Math.Max(shortX / 32767.0 - DeadZone, 0.0) / (1 - DeadZone));
            y = (float) (Math.Max(shortY / 32767.0 - DeadZone, 0.0) / (1 - DeadZone));
            retDirection = new Vector2(x, y);
            return retDirection;
        }
    }
}