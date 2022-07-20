using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sentry.Unity.NativeUtils
{
    internal static class C
    {
        internal static void SetValueIfNotNull(sentry_value_t obj, string key, string? value)
        {
            if (value is not null)
            {
                _ = sentry_value_set_by_key(obj, key, sentry_value_new_string(value));
            }
        }

        internal static void SetValueIfNotNull(sentry_value_t obj, string key, int? value)
        {
            if (value.HasValue)
            {
                _ = sentry_value_set_by_key(obj, key, sentry_value_new_int32(value.Value));
            }
        }

        internal static void SetValueIfNotNull(sentry_value_t obj, string key, bool? value)
        {
            if (value.HasValue)
            {
                _ = sentry_value_set_by_key(obj, key, sentry_value_new_bool(value.Value ? 1 : 0));
            }
        }

        internal static void SetValueIfNotNull(sentry_value_t obj, string key, double? value)
        {
            if (value.HasValue)
            {
                _ = sentry_value_set_by_key(obj, key, sentry_value_new_double(value.Value));
            }
        }

        internal static sentry_value_t? GetValueOrNul(sentry_value_t obj, string key)
        {
            var cValue = sentry_value_get_by_key(obj, key);
            return sentry_value_is_null(cValue) == 0 ? cValue : null;
        }

        internal static string? GetValueString(sentry_value_t obj, string key)
        {
            if (GetValueOrNul(obj, key) is { } cValue)
            {
                return sentry_value_as_string(cValue);
            }
            return null;
        }

        internal static long? GetValueLong(sentry_value_t obj, string key)
        {
            if (GetValueOrNul(obj, key) is { } cValue)
            {
                return sentry_value_as_int32(cValue);
            }
            return null;
        }

        internal static double? GetValueDouble(sentry_value_t obj, string key)
        {
            if (GetValueOrNul(obj, key) is { } cValue)
            {
                return sentry_value_as_double(cValue);
            }
            return null;
        }

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_object();

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_null();

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_bool(int value);

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_double(double value);

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_int32(int value);

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_string(string value);

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_new_breadcrumb(string? type, string? message);

        [DllImport("sentry")]
        internal static extern int sentry_value_set_by_key(sentry_value_t value, string k, sentry_value_t v);

        internal static bool IsNull(sentry_value_t value) => sentry_value_is_null(value) != 0;

        [DllImport("sentry")]
        internal static extern int sentry_value_is_null(sentry_value_t value);

        [DllImport("sentry")]
        internal static extern int sentry_value_as_int32(sentry_value_t value);

        [DllImport("sentry")]
        internal static extern double sentry_value_as_double(sentry_value_t value);

        [DllImport("sentry")]
        internal static extern string sentry_value_as_string(sentry_value_t value);

        [DllImport("sentry")]
        internal static extern UIntPtr sentry_value_get_length(sentry_value_t value);

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_get_by_index(sentry_value_t value, UIntPtr index);

        [DllImport("sentry")]
        internal static extern sentry_value_t sentry_value_get_by_key(sentry_value_t value, string key);

        [DllImport("sentry")]
        internal static extern void sentry_set_context(string key, sentry_value_t value);

        [DllImport("sentry")]
        internal static extern void sentry_add_breadcrumb(sentry_value_t breadcrumb);

        [DllImport("sentry")]
        internal static extern void sentry_set_tag(string key, string value);

        [DllImport("sentry")]
        internal static extern void sentry_remove_tag(string key);

        [DllImport("sentry")]
        internal static extern void sentry_set_user(sentry_value_t user);

        [DllImport("sentry")]
        internal static extern void sentry_remove_user();

        [DllImport("sentry")]
        internal static extern void sentry_set_extra(string key, sentry_value_t value);

        [DllImport("sentry")]
        internal static extern void sentry_remove_extra(string key);

        internal static Lazy<IEnumerable<DebugImage>> DebugImages = new(LoadDebugImages);

        private static IEnumerable<DebugImage> LoadDebugImages()
        {
            var cList = sentry_get_modules_list();
            try
            {
                var result = new List<DebugImage>();

                if (!IsNull(cList))
                {
                    var len = sentry_value_get_length(cList).ToUInt32();
                    for (uint i = 0; i < len; i++)
                    {
                        var cItem = sentry_value_get_by_index(cList, (UIntPtr)i);
                        if (!IsNull(cItem))
                        {
                            var image = new DebugImage();

                            // See possible values in
                            // * https://github.com/getsentry/sentry-native/blob/8faa78298da68d68043f0c3bd694f756c0e95dfa/src/modulefinder/sentry_modulefinder_windows.c#L81
                            // * https://github.com/getsentry/sentry-native/blob/8faa78298da68d68043f0c3bd694f756c0e95dfa/src/modulefinder/sentry_modulefinder_windows.c#L24
                            // * https://github.com/getsentry/sentry-native/blob/c5c31e56d36bed37fa5422750a591f44502edb41/src/modulefinder/sentry_modulefinder_linux.c#L465
                            image.CodeFile = GetValueString(cItem, "code_file");
                            image.ImageAddress = GetValueString(cItem, "image_addr");
                            image.ImageSize = GetValueLong(cItem, "image_size");
                            image.DebugFile = GetValueString(cItem, "debug_file");
                            image.DebugId = GetValueString(cItem, "debug_id");
                            image.CodeId = GetValueString(cItem, "code_id");
                            image.Type = GetValueString(cItem, "type");

                            result.Add(image);
                        }
                    }
                }
                return result;
            }
            finally
            {
                sentry_value_decref(cList);
            }
        }

        // Returns a new reference to an immutable, frozen list.
        // The reference must be released with `sentry_value_decref`.
        [DllImport("sentry")]
        private static extern sentry_value_t sentry_get_modules_list();

        [DllImport("sentry")]
        internal static extern void sentry_value_decref(sentry_value_t value);

        // native union sentry_value_u/t
        [StructLayout(LayoutKind.Explicit)]
        internal struct sentry_value_t
        {
            [FieldOffset(0)]
            internal ulong _bits;
            [FieldOffset(0)]
            internal double _double;
        }

    }
}
