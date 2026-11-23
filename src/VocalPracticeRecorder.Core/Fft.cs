using System.Numerics;

namespace VocalPracticeRecorder.Core;

/// <summary>Minimal in-place radix-2 complex FFT.</summary>
public static class Fft
{
    public static int NextPow2(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    public static void Transform(Complex[] data, bool inverse)
    {
        int n = data.Length;
        if ((n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            var wlen = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                int half = len >> 1;
                for (int k = 0; k < half; k++)
                {
                    Complex u = data[i + k];
                    Complex v = data[i + k + half] * w;
                    data[i + k] = u + v;
                    data[i + k + half] = u - v;
                    w *= wlen;
                }
            }
        }

        if (inverse)
        {
            for (int i = 0; i < n; i++) data[i] /= n;
        }
    }
}
