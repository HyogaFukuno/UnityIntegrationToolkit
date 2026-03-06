#define FILLATTRIBUTES // Uncomment this line to fill the attributes
//#define INTERNAL      // Uncomment this line to make the class internal
#define TMP_INTERPOTATION_EXTENSIONS // Uncomment this line to add TMP extensions

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
#if TMP_INTERPOTATION_EXTENSIONS
using TMPro;
#endif

namespace System.Runtime.CompilerServices
{
    /// <summary>Provides a handler used by the language compiler to process interpolated strings into <see cref="string"/> instances.</summary>
    [InterpolatedStringHandler]
#if INTERNAL
    internal
#else
    public
#endif

        ref struct DefaultInterpolatedStringHandler
    {
        // Implementation note:
        // As this type lives in CompilerServices and is only intended to be targeted by the compiler,
        // public APIs eschew argument validation logic in a variety of places, e.g. allowing a null input
        // when one isn't expected to produce a NullReferenceException rather than an ArgumentNullException.

        /// <summary>Expected average length of formatted data used for an individual interpolation expression result.</summary>
        /// <remarks>
        /// This is inherited from string.Format, and could be changed based on further data.
        /// string.Format actually uses `format.Length + args.Length * 8`, but format.Length
        /// includes the format items themselves, e.g. "{0}", and since it's rare to have double-digit
        /// numbers of items, we bump the 8 up to 11 to account for the three extra characters in "{d}",
        /// since the compiler-provided base length won't include the equivalent character count.
        /// </remarks>
        const int GuessedLengthPerHole = 11;

        /// <summary>Minimum size array to rent from the pool.</summary>
        /// <remarks>Same as stack-allocation size used today by string.Format.</remarks>
        const int MinimumArrayPoolLength = 256;

        /// <summary>Optional provider to pass to IFormattable.ToString or ISpanFormattable.TryFormat calls.</summary>
        readonly IFormatProvider? provider;

        /// <summary>Array rented from the array pool and used to back <see cref="_chars"/>.</summary>
        char[]? arrayToReturnToPool;

        /// <summary>The span to write into.</summary>
        Span<char> chars;

        /// <summary>Position at which to write the next character.</summary>
        int pos;

        /// <summary>Whether <see cref="_provider"/> provides an ICustomFormatter.</summary>
        /// <remarks>
        /// Custom formatters are very rare.  We want to support them, but it's ok if we make them more expensive
        /// in order to make them as pay-for-play as possible.  So, we avoid adding another reference type field
        /// to reduce the size of the handler and to reduce required zero'ing, by only storing whether the provider
        /// provides a formatter, rather than actually storing the formatter.  This in turn means, if there is a
        /// formatter, we pay for the extra interface call on each AppendFormatted that needs it.
        /// </remarks>
        readonly bool hasCustomFormatter;

        /// <summary>Creates a handler used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of interpolation expressions in the interpolated string.</param>
        /// <param name="formattedCount">The number of interpolation expressions in the interpolated string.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public DefaultInterpolatedStringHandler(int literalLength, int formattedCount)
        {
            provider = null;
            chars = arrayToReturnToPool = ArrayPool<char>.Shared.Rent(GetDefaultLength(literalLength, formattedCount));
            pos = 0;
            hasCustomFormatter = false;
        }

        /// <summary>Creates a handler used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of interpolation expressions in the interpolated string.</param>
        /// <param name="formattedCount">The number of interpolation expressions in the interpolated string.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public DefaultInterpolatedStringHandler(int literalLength, int formattedCount, IFormatProvider? provider)
        {
            this.provider = provider;
            chars = arrayToReturnToPool = ArrayPool<char>.Shared.Rent(GetDefaultLength(literalLength, formattedCount));
            pos = 0;
            hasCustomFormatter = provider is not null && HasCustomFormatter(provider);
        }

        /// <summary>Creates a handler used to translate an interpolated string into a <see cref="string"/>.</summary>
        /// <param name="literalLength">The number of constant characters outside of interpolation expressions in the interpolated string.</param>
        /// <param name="formattedCount">The number of interpolation expressions in the interpolated string.</param>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="initialBuffer">A buffer temporarily transferred to the handler for use as part of its formatting.  Contents may be overwritten.</param>
        /// <remarks>This is intended to be called only by compiler-generated code. Arguments are not validated as they'd otherwise be for members intended to be used directly.</remarks>
        public DefaultInterpolatedStringHandler(int literalLength, int formattedCount, IFormatProvider? provider, Span<char> initialBuffer)
        {
            this.provider = provider;
            chars = initialBuffer;
            arrayToReturnToPool = null;
            pos = 0;
            hasCustomFormatter = provider is not null && HasCustomFormatter(provider);
        }

        /// <summary>Derives a default length with which to seed the handler.</summary>
        /// <param name="literalLength">The number of constant characters outside of interpolation expressions in the interpolated string.</param>
        /// <param name="formattedCount">The number of interpolation expressions in the interpolated string.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] // becomes a constant when inputs are constant
        internal static int GetDefaultLength(int literalLength, int formattedCount) =>
            Math.Max(MinimumArrayPoolLength, literalLength + (formattedCount * GuessedLengthPerHole));

        /// <summary>Gets the built <see cref="string"/>.</summary>
        /// <returns>The built string.</returns>
        public override string ToString() => new string(Text);

        /// <summary>Gets the built <see cref="string"/> and clears the handler.</summary>
        /// <returns>The built string.</returns>
        /// <remarks>
        /// This releases any resources used by the handler. The method should be invoked only
        /// once and as the last thing performed on the handler. Subsequent use is erroneous, ill-defined,
        /// and may destabilize the process, as may using any other copies of the handler after ToStringAndClear
        /// is called on any one of them.
        /// </remarks>
        public string ToStringAndClear()
        {
            string result = new string(Text);
            Clear();
            return result;
        }

        /// <summary>Clears the handler, returning any rented array to the pool.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] // used only on a few hot paths
        internal void Clear()
        {
            char[]? toReturn = arrayToReturnToPool;
            this = default; // defensive clear
            if (toReturn is not null)
            {
                ArrayPool<char>.Shared.Return(toReturn);
            }
        }

        /// <summary>Gets a span of the written characters thus far.</summary>
        internal ReadOnlySpan<char> Text => chars.Slice(0, pos);

        /// <summary>Writes the specified string to the handler.</summary>
        /// <param name="value">The string to write.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AppendLiteral(string value)
        {
            if (value.AsSpan().TryCopyTo(chars.Slice(pos)))
            {
                pos += value.Length;
            }
            else
            {
                GrowThenCopyString(value);
            }
        }

        #region AppendFormatted

         /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        public void AppendFormatted(int value)
        {
            AppendFormatted(value, null);
        }

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(int value, string? format)
        {
            if (hasCustomFormatter)
            {
                AppendCustomFormatter(value, format);
                return;
            }
            int charsWritten;
            while (!value.TryFormat(chars.Slice(pos), out charsWritten, format, provider))
            {
                Grow();
            }
            pos += charsWritten;
        }

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        public void AppendFormatted(float value)
        {
            AppendFormatted(value, null);
        }

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(float value, string? format)
        {
            if (hasCustomFormatter)
            {
                AppendCustomFormatter(value, format);
                return;
            }
            int charsWritten;
            while (!value.TryFormat(chars.Slice(pos), out charsWritten, format, provider))
            {
                Grow();
            }
            pos += charsWritten;
        }

        // Design note:
        // The compiler requires a AppendFormatted overload for anything that might be within an interpolation expression;
        // if it can't find an appropriate overload, for handlers in general it'll simply fail to compile.
        // (For target-typing to string where it uses DefaultInterpolatedStringHandler implicitly, it'll instead fall back to
        // its other mechanisms, e.g. using string.Format.  This fallback has the benefit that if we miss a case,
        // interpolated strings will still work, but it has the downside that a developer generally won't know
        // if the fallback is happening and they're paying more.)
        //
        // At a minimum, then, we would need an overload that accepts:
        //     (object value, int alignment = 0, string? format = null)
        // Such an overload would provide the same expressiveness as string.Format.  However, this has several
        // shortcomings:
        // - Every value type in an interpolation expression would be boxed.
        // - ReadOnlySpan<char> could not be used in interpolation expressions.
        // - Every AppendFormatted call would have three arguments at the call site, bloating the IL further.
        // - Every invocation would be more expensive, due to lack of specialization, every call needing to account
        //   for alignment and format, etc.
        //
        // To address that, we could just have overloads for T and ReadOnlySpan<char>:
        //     (T)
        //     (T, int alignment)
        //     (T, string? format)
        //     (T, int alignment, string? format)
        //     (ReadOnlySpan<char>)
        //     (ReadOnlySpan<char>, int alignment)
        //     (ReadOnlySpan<char>, string? format)
        //     (ReadOnlySpan<char>, int alignment, string? format)
        // but this also has shortcomings:
        // - Some expressions that would have worked with an object overload will now force a fallback to string.Format
        //   (or fail to compile if the handler is used in places where the fallback isn't provided), because the compiler
        //   can't always target type to T, e.g. `b switch { true => 1, false => null }` where `b` is a bool can successfully
        //   be passed as an argument of type `object` but not of type `T`.
        // - Reference types get no benefit from going through the generic code paths, and actually incur some overheads
        //   from doing so.
        // - Nullable value types also pay a heavy price, in particular around interface checks that would generally evaporate
        //   at compile time for value types but don't (currently) if the Nullable<T> goes through the same code paths
        //   (see https://github.com/dotnet/runtime/issues/50915).
        //
        // We could try to take a more elaborate approach for DefaultInterpolatedStringHandler, since it is the most common handler
        // and we want to minimize overheads both at runtime and in IL size, e.g. have a complete set of overloads for each of:
        //     (T, ...) where T : struct
        //     (T?, ...) where T : struct
        //     (object, ...)
        //     (ReadOnlySpan<char>, ...)
        //     (string, ...)
        // but this also has shortcomings, most importantly:
        // - If you have an unconstrained T that happens to be a value type, it'll now end up getting boxed to use the object overload.
        //   This also necessitates the T? overload, since nullable value types don't meet a T : struct constraint, so without those
        //   they'd all map to the object overloads as well.
        // - Any reference type with an implicit cast to ROS<char> will fail to compile due to ambiguities between the overloads. string
        //   is one such type, hence needing dedicated overloads for it that can be bound to more tightly.
        //
        // A middle ground we've settled on, which is likely to be the right approach for most other handlers as well, would be the set:
        //     (T, ...) with no constraint
        //     (ReadOnlySpan<char>) and (ReadOnlySpan<char>, int)
        //     (object, int alignment = 0, string? format = null)
        //     (string) and (string, int)
        // This would address most of the concerns, at the expense of:
        // - Most reference types going through the generic code paths and so being a bit more expensive.
        // - Nullable types being more expensive until https://github.com/dotnet/runtime/issues/50915 is addressed.
        //   We could choose to add a T? where T : struct set of overloads if necessary.
        // Strings don't require their own overloads here, but as they're expected to be very common and as we can
        // optimize them in several ways (can copy the contents directly, don't need to do any interface checks, don't
        // need to pay the shared generic overheads, etc.) we can add overloads specifically to optimize for them.
        //
        // Hole values are formatted according to the following policy:
        // 1. If an IFormatProvider was supplied and it provides an ICustomFormatter, use ICustomFormatter.Format (even if the value is null).
        // 2. If the type implements ISpanFormattable, use ISpanFormattable.TryFormat.
        // 3. If the type implements IFormattable, use IFormattable.ToString.
        // 4. Otherwise, use object.ToString.
        // This matches the behavior of string.Format, StringBuilder.AppendFormat, etc.  The only overloads for which this doesn't
        // apply is ReadOnlySpan<char>, which isn't supported by either string.Format nor StringBuilder.AppendFormat, but more
        // importantly which can't be boxed to be passed to ICustomFormatter.Format.

        #region AppendFormatted T

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <typeparam name="T">The type of the value to write.</typeparam>
        public void AppendFormatted<T>(T value)
        {
            AppendFormatted(value, null);
        }


        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        /// <typeparam name="T">The type of the value to write.</typeparam>
        public void AppendFormatted<T>(T value, string? format)
        {
            if (hasCustomFormatter)
            {
                AppendCustomFormatter(value, format);
                return;
            }
            if (SpanFormatterCache<T>.HasTryFormat)
            {
                var tryFormatStruct = SpanFormatterCache<T>.TryFormatStructDelegate;
                if (tryFormatStruct != null)
                {
                    int charsWritten;
                    while (!tryFormatStruct(value, chars.Slice(pos), out charsWritten, format, provider))
                    {
                        Grow();
                    }
                    pos += charsWritten;
                    return;
                }
                var tryFormatClass = SpanFormatterCache<T>.TryFormatClassDelegate!;
                {
                    int charsWritten;
                    while (!tryFormatClass(value, chars.Slice(pos), out charsWritten, format, provider))
                    {
                        Grow();
                    }
                    pos += charsWritten;
                    return;
                }
            }
            string? s;
            if (value is IFormattable formattable)
            {
                s = (formattable).ToString(format, provider);
            }
            else
            {
                s = value?.ToString();
            }
            if (s is not null)
            {
                AppendLiteral(s);
            }
        }


        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <typeparam name="T">The type of the value to write.</typeparam>
        public void AppendFormatted<T>(T value, int alignment)
        {
            int startingPos = pos;
            AppendFormatted(value);
            if (alignment != 0)
            {
                AppendOrInsertAlignmentIfNeeded(startingPos, alignment);
            }
        }

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <typeparam name="T">The type of the value to write.</typeparam>
        public void AppendFormatted<T>(T value, int alignment, string? format)
        {
            int startingPos = pos;
            AppendFormatted(value, format);
            if (alignment != 0)
            {
                AppendOrInsertAlignmentIfNeeded(startingPos, alignment);
            }
        }

        #endregion

        #region AppendFormatted ReadOnlySpan<char>

        /// <summary>Writes the specified character span to the handler.</summary>
        /// <param name="value">The span to write.</param>
        public void AppendFormatted(ReadOnlySpan<char> value)
        {
            if (value.TryCopyTo(chars.Slice(pos)))
            {
                pos += value.Length;
            }
            else
            {
                GrowThenCopySpan(value);
            }
        }

        /// <summary>Writes the specified string of chars to the handler.</summary>
        /// <param name="value">The span to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null)
        {
            bool leftAlign = false;
            if (alignment < 0)
            {
                leftAlign = true;
                alignment = -alignment;
            }
            int paddingRequired = alignment - value.Length;
            if (paddingRequired <= 0)
            {
                AppendFormatted(value);
                return;
            }
            EnsureCapacityForAdditionalChars(value.Length + paddingRequired);
            if (leftAlign)
            {
                value.CopyTo(chars.Slice(pos));
                pos += value.Length;
                chars.Slice(pos, paddingRequired).Fill(' ');
                pos += paddingRequired;
            }
            else
            {
                chars.Slice(pos, paddingRequired).Fill(' ');
                pos += paddingRequired;
                value.CopyTo(chars.Slice(pos));
                pos += value.Length;
            }
        }

        #endregion

        #region AppendFormatted string

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        public void AppendFormatted(string? value)
        {
            if (!hasCustomFormatter &&
                value is not null &&
                value.AsSpan().TryCopyTo(chars.Slice(pos)))
            {
                pos += value.Length;
            }
            else
            {
                AppendFormattedSlow(value);
            }
        }

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <remarks>
        /// Slow path to handle a custom formatter, potentially null value,
        /// or a string that doesn't fit in the current buffer.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AppendFormattedSlow(string? value)
        {
            if (hasCustomFormatter)
            {
                AppendCustomFormatter(value, format: null);
            }
            else if (value is not null)
            {
                EnsureCapacityForAdditionalChars(value.Length);
                value.AsSpan().CopyTo(chars.Slice(pos));
                pos += value.Length;
            }
        }

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
            AppendFormatted<string?>(value, alignment, format);

        #endregion

        #region AppendFormatted object

        /// <summary>Writes the specified value to the handler.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        /// <param name="format">The format string.</param>
        public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
            AppendFormatted<object?>(value, alignment, format);

        #endregion

        #endregion

        /// <summary>Gets whether the provider provides a custom formatter.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] // only used in a few hot path call sites
        internal static bool HasCustomFormatter(IFormatProvider provider)
        {
            Debug.Assert(provider is not null);
            Debug.Assert(provider is not CultureInfo || provider.GetFormat(typeof(ICustomFormatter)) is null, "Expected CultureInfo to not provide a custom formatter");
            return
                provider.GetType() != typeof(CultureInfo) &&
                provider.GetFormat(typeof(ICustomFormatter)) != null;
        }

        /// <summary>Formats the value using the custom formatter from the provider.</summary>
        /// <param name="value">The value to write.</param>
        /// <param name="format">The format string.</param>
        /// <typeparam name="T">The type of the value to write.</typeparam>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AppendCustomFormatter<T>(T value, string? format)
        {
            Debug.Assert(hasCustomFormatter);
            Debug.Assert(provider != null);
            ICustomFormatter? formatter = (ICustomFormatter?)provider.GetFormat(typeof(ICustomFormatter));
            Debug.Assert(formatter != null, "An incorrectly written provider said it implemented ICustomFormatter, and then didn't");
            if (formatter is not null && formatter.Format(format, value, provider) is string customFormatted)
            {
                AppendLiteral(customFormatted);
            }
        }

        /// <summary>Handles adding any padding required for aligning a formatted value in an interpolation expression.</summary>
        /// <param name="startingPos">The position at which the written value started.</param>
        /// <param name="alignment">Non-zero minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
        private void AppendOrInsertAlignmentIfNeeded(int startingPos, int alignment)
        {
            Debug.Assert(startingPos >= 0 && startingPos <= pos);
            Debug.Assert(alignment != 0);
            int charsWritten = pos - startingPos;
            bool leftAlign = false;
            if (alignment < 0)
            {
                leftAlign = true;
                alignment = -alignment;
            }
            int paddingNeeded = alignment - charsWritten;
            if (paddingNeeded > 0)
            {
                EnsureCapacityForAdditionalChars(paddingNeeded);
                if (leftAlign)
                {
                    chars.Slice(pos, paddingNeeded).Fill(' ');
                }
                else
                {
                    chars.Slice(startingPos, charsWritten).CopyTo(chars.Slice(startingPos + paddingNeeded));
                    chars.Slice(startingPos, paddingNeeded).Fill(' ');
                }
                pos += paddingNeeded;
            }
        }

        /// <summary>Ensures <see cref="_chars"/> has the capacity to store <paramref name="additionalChars"/> beyond <see cref="_pos"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacityForAdditionalChars(int additionalChars)
        {
            if (chars.Length - pos < additionalChars)
            {
                Grow(additionalChars);
            }
        }

        /// <summary>Fallback for fast path in <see cref="AppendLiteral(string)"/> when there's not enough space in the destination.</summary>
        /// <param name="value">The string to write.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowThenCopyString(string value)
        {
            Grow(value.Length);
            value.AsSpan().CopyTo(chars.Slice(pos));
            pos += value.Length;
        }

        /// <summary>Fallback for <see cref="AppendFormatted(ReadOnlySpan{char})"/> for when not enough space exists in the current buffer.</summary>
        /// <param name="value">The span to write.</param>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void GrowThenCopySpan(ReadOnlySpan<char> value)
        {
            Grow(value.Length);
            value.CopyTo(chars.Slice(pos));
            pos += value.Length;
        }

        /// <summary>Grows <see cref="_chars"/> to have the capacity to store at least <paramref name="additionalChars"/> beyond <see cref="_pos"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // keep consumers as streamlined as possible
        private void Grow(int additionalChars)
        {
            Debug.Assert(additionalChars > chars.Length - pos);
            GrowCore((uint)pos + (uint)additionalChars);
        }

        /// <summary>Grows the size of <see cref="_chars"/>.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)] // keep consumers as streamlined as possible
        private void Grow()
        {
            GrowCore((uint)chars.Length + 1);
        }

        /// <summary>Grow the size of <see cref="_chars"/> to at least the specified <paramref name="requiredMinCapacity"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)] // but reuse this grow logic directly in both of the above grow routines
        private void GrowCore(uint requiredMinCapacity)
        {
            uint newCapacity = Math.Max(requiredMinCapacity, Math.Min((uint)chars.Length * 2, 100000));
            int arraySize = (int)Math.Clamp(newCapacity, MinimumArrayPoolLength, int.MaxValue);
            char[] newArray = ArrayPool<char>.Shared.Rent(arraySize);
            chars.Slice(0, pos).CopyTo(newArray);
            char[]? toReturn = arrayToReturnToPool;
            chars = arrayToReturnToPool = newArray;
            if (toReturn is not null)
            {
                ArrayPool<char>.Shared.Return(toReturn);
            }
        }

        public (char[] Array, int Length) GetArrayAndLength()
        {
            return (arrayToReturnToPool!, pos);
        }
    }

    public delegate bool TryFormatStruct<T>(in T value, Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default(ReadOnlySpan<char>),
        IFormatProvider? provider = null);

    public delegate bool TryFormatClass<T>(T value, Span<char> destination,
        out int charsWritten,
        ReadOnlySpan<char> format = default(ReadOnlySpan<char>),
        IFormatProvider? provider = null);

    public static class SpanFormatterCache
    {
        static readonly Assembly assembly = typeof(int).Assembly;
        static readonly Type iSpanFormattable = assembly.GetType("System.ISpanFormattable");
        static readonly Type[] arguments = new Type[] { typeof(Span<char>), typeof(int).MakeByRefType(), typeof(ReadOnlySpan<char>), typeof(IFormatProvider) };


        public static bool TryGetSpanFormmatableDelegate<T>(out TryFormatStruct<T> tryFormatStruct, out TryFormatClass<T> tryFormatClass)
        {
            var t = typeof(T);
            if (iSpanFormattable.IsAssignableFrom(t))
            {
                var method = t.GetMethod("TryFormat", 0, arguments, null);
                if (method == null)
                {
                    tryFormatStruct = null!;
                    tryFormatClass = null!;
                    return false;
                }

                if (t.IsValueType)
                {
                    tryFormatStruct = (TryFormatStruct<T>)method!.CreateDelegate(typeof(TryFormatStruct<T>));
                    tryFormatClass = null!;
                }
                else
                {
                    tryFormatClass = (TryFormatClass<T>)method!.CreateDelegate(typeof(TryFormatClass<T>));
                    tryFormatStruct = null!;
                }

                return true;
            }

            tryFormatStruct = null!;
            tryFormatClass = null!;
            return false;
        }
    }
#if FILLATTRIBUTES
    /// <summary>Indicates the attributed type is to be used as an interpolated string handler.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
#if INTERNAL
    internal
#else
    public
#endif
        sealed class InterpolatedStringHandlerAttribute : Attribute
    {
        /// <summary>Initializes the <see cref="InterpolatedStringHandlerAttribute"/>.</summary>
        public InterpolatedStringHandlerAttribute()
        {
        }
    }

    /// <summary>Indicates which arguments to a method involving an interpolated string handler should be passed to that handler.</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]

#if INTERNAL
    internal
#else
    public
#endif

        sealed class InterpolatedStringHandlerArgumentAttribute : Attribute
    {
        /// <summary>Initializes a new instance of the <see cref="InterpolatedStringHandlerArgumentAttribute"/> class.</summary>
        /// <param name="argument">The name of the argument that should be passed to the handler.</param>
        /// <remarks>The empty string may be used as the name of the receiver in an instance method.</remarks>
        public InterpolatedStringHandlerArgumentAttribute(string argument) => Arguments = new[] { argument };

        /// <summary>Initializes a new instance of the <see cref="InterpolatedStringHandlerArgumentAttribute"/> class.</summary>
        /// <param name="arguments">The names of the arguments that should be passed to the handler.</param>
        /// <remarks>The empty string may be used as the name of the receiver in an instance method.</remarks>
        public InterpolatedStringHandlerArgumentAttribute(params string[] arguments) => Arguments = arguments;

        /// <summary>Gets the names of the arguments that should be passed to the handler.</summary>
        /// <remarks>The empty string may be used as the name of the receiver in an instance method.</remarks>
        public string[] Arguments { get; }
    }
//Following codes are licensed under CC0.
#endif
#if INTERNAL
    internal
#else
    public
#endif
        static class SpanFormatterCache<T>
    {
        static TryFormatStruct<T>? tryFormatStruct;
        static TryFormatClass<T>? tryFormatClass;

        public static TryFormatStruct<T>? TryFormatStructDelegate => tryFormatStruct;
        public static TryFormatClass<T>? TryFormatClassDelegate => tryFormatClass;

        static SpanFormatterCache()
        {
            SpanFormatterCache.TryGetSpanFormmatableDelegate<T>(out tryFormatStruct, out tryFormatClass);
        }

        public static bool HasTryFormat => tryFormatStruct != null || tryFormatClass != null;

        public static bool TrySetTryFormat(TryFormatStruct<T> tryFormat)
        {
            if (typeof(T).IsClass) throw new Exception();
            if (tryFormatStruct != null)
                return false;
            tryFormatStruct = tryFormat;
            return true;
        }

        public static bool TrySetTryFormat(TryFormatClass<T> tryFormat)
        {
            if (!typeof(T).IsClass) throw new Exception();
            if (tryFormatClass != null)
                return false;
            tryFormatClass = tryFormat;
            return true;
        }
    }
}
#if TMP_INTERPOTATION_EXTENSIONS

#if INTERNAL
    internal
#else
public
#endif

    static class TMPInterpolatedStringHandlerExtensions
{
    public static void SetTextFormat(this TMP_Text text, ref DefaultInterpolatedStringHandler handler)
    {
        var (array, length) = handler.GetArrayAndLength();
        text.SetText(array, 0, length);
        handler.Clear();
    }
}

#endif

#if INTERNAL
    internal
#else
public
#endif

    static class StringBuilderExtensions
{
    public static void AppendFormatted(this StringBuilder text, ref DefaultInterpolatedStringHandler handler)
    {
        var (array, length) = handler.GetArrayAndLength();
        text.Append(array, 0, length);
        handler.Clear();
    }
    
    public static void AppendLineFormatted(this StringBuilder text, ref DefaultInterpolatedStringHandler handler)
    {
        var (array, length) = handler.GetArrayAndLength();
        text.Append(array, 0, length);
        handler.Clear();
        text.AppendLine();
    }
}

