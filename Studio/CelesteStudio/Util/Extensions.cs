using System;
using System.Collections.Generic;

namespace CelesteStudio.Util;

public static class Extensions
{
    public static int Digits(this int self) => Math.Abs(self).ToString().Length;
    
    public static int IndexOf<T>(this IEnumerable<T> obj, T value) => obj.IndexOf(value, EqualityComparer<T>.Default);
    public static int IndexOf<T>(this IEnumerable<T> obj, T value, IEqualityComparer<T> comparer)
    {
        using var iter = obj.GetEnumerator();
        
        int i = 0;
        while (iter.MoveNext())
        {
            if (comparer.Equals(iter.Current, value))
                return i;
            i++;
        }
        
        return -1;
    }
}