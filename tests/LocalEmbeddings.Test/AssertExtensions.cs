// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LocalEmbeddings.Test;

internal static class AssertExtensions
{
    public static void Equals<T>(T expected, T actual, string? message = null)
    {
        if (expected is IEnumerable<T> expectedEnumerable && actual is IEnumerable<T> actualEnumerable)
        {
            var expectedList = expectedEnumerable.ToList();
            var actualList = actualEnumerable.ToList();

            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
                expectedList.Count,
                actualList.Count,
                $"Collection count mismatch. {message}");

            for (int i = 0; i < expectedList.Count; i++)
            {
                Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(
                    expectedList[i],
                    actualList[i],
                    $"Item at index {i} does not match. {message}");
            }
        }
        else
        {
            Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(expected, actual, message);
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreEqual(expected, actual, message);
    }

    public static void NotEqual<T>(T expected, T actual, string? message = null)
    {
        Microsoft.VisualStudio.TestTools.UnitTesting.Assert.AreNotEqual(expected, actual, message);
    }

    public static void InRange<T>(T actual, T min, T max) where T : IComparable<T>
    {
        if (actual.CompareTo(min) < 0 || actual.CompareTo(max) > 0)
        {
            throw new AssertFailedException(
                $"Value {actual} is not in range [{min}, {max}]");
        }
    }

    public static void Collection<T>(IEnumerable<T> collection, params Action<T>[] inspectors)
    {
        var list = collection.ToList();

        if (list.Count != inspectors.Length)
        {
            throw new AssertFailedException(
                $"Collection has {list.Count} items but {inspectors.Length} inspectors were provided");
        }

        for (int i = 0; i < list.Count; i++)
        {
            try
            {
                inspectors[i](list[i]);
            }
            catch (Exception ex)
            {
                throw new AssertFailedException(
                    $"Inspector at index {i} failed: {ex.Message}", ex);
            }
        }
    }
}
