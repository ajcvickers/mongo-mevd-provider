// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoServiceCollectionExtensions"/>.
/// </summary>
public sealed class MongoServiceCollectionExtensionsTests
{
    [Fact]
    public void TrimAndAotAttributeMessagesAreNotSwapped()
    {
        // Every annotated DI extension must carry the trimming message on [RequiresUnreferencedCode] (IL2026)
        // and the NativeAOT message on [RequiresDynamicCode] (IL3050) — not the other way around.
        var methods = typeof(MongoServiceCollectionExtensions)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<RequiresUnreferencedCodeAttribute>() is not null
                     || m.GetCustomAttribute<RequiresDynamicCodeAttribute>() is not null)
            .ToList();

        Assert.NotEmpty(methods);

        foreach (var method in methods)
        {
            var unreferencedCode = method.GetCustomAttribute<RequiresUnreferencedCodeAttribute>();
            var dynamicCode = method.GetCustomAttribute<RequiresDynamicCodeAttribute>();

            Assert.NotNull(unreferencedCode);
            Assert.NotNull(dynamicCode);

            Assert.True(
                unreferencedCode!.Message?.Contains("incompatible with trimming") == true,
                $"{method.Name}: [RequiresUnreferencedCode] should carry the trimming message but was: '{unreferencedCode.Message}'");

            Assert.True(
                dynamicCode!.Message?.Contains("incompatible with NativeAOT") == true,
                $"{method.Name}: [RequiresDynamicCode] should carry the NativeAOT message but was: '{dynamicCode.Message}'");
        }
    }
}
