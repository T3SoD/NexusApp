using System;
using System.Collections.Generic;
using NexusApp.Services;
using Xunit;

namespace NexusApp.Tests;

// Pure bucketing logic for the SCMDB import (issue #3 design point 3): parsed export names +
// an owned-set + a name-resolution delegate -> toImport / alreadyOwned / unrecognized buckets.
// The resolution delegate stands in for the real official-name + localization pipeline
// GameLogBlueprintImporter uses; Task 2 plugs that in, these tests use plain dictionaries.
public class ScmdbImportPlanTests
{
    private static Func<string, string?> Resolver(params (string raw, string canonical)[] map)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (raw, canonical) in map) dict[raw] = canonical;
        return raw => dict.TryGetValue(raw, out var canon) ? canon : null;
    }

    [Fact]
    public void Build_NewResolvedName_GoesToToImport()
    {
        var resolve = Resolver(("Arclight Pistol", "Arclight Pistol"));
        var result = ScmdbImportPlan.Build(new[] { "Arclight Pistol" }, Array.Empty<string>(), resolve);

        Assert.Equal(new[] { "Arclight Pistol" }, result.ToImport);
        Assert.Empty(result.AlreadyOwned);
        Assert.Empty(result.Unrecognized);
    }

    [Fact]
    public void Build_AlreadyOwnedResolvedName_GoesToAlreadyOwnedNotToImport()
    {
        var resolve = Resolver(("Coda Pistol", "Coda Pistol"));
        var result = ScmdbImportPlan.Build(new[] { "Coda Pistol" }, new[] { "Coda Pistol" }, resolve);

        Assert.Empty(result.ToImport);
        Assert.Equal(new[] { "Coda Pistol" }, result.AlreadyOwned);
        Assert.Empty(result.Unrecognized);
    }

    [Fact]
    public void Build_OwnedSetMatch_IsCaseInsensitive()
    {
        var resolve = Resolver(("Coda Pistol", "Coda Pistol"));
        var result = ScmdbImportPlan.Build(new[] { "Coda Pistol" }, new[] { "coda pistol" }, resolve);

        Assert.Empty(result.ToImport);
        Assert.Equal(new[] { "Coda Pistol" }, result.AlreadyOwned);
    }

    [Fact]
    public void Build_UnresolvedName_GoesToUnrecognized()
    {
        var resolve = Resolver(); // resolves nothing
        var result = ScmdbImportPlan.Build(new[] { "Nonexistent Test Widget" }, Array.Empty<string>(), resolve);

        Assert.Empty(result.ToImport);
        Assert.Empty(result.AlreadyOwned);
        Assert.Equal(new[] { "Nonexistent Test Widget" }, result.Unrecognized);
    }

    [Fact]
    public void Build_MixOfNewOwnedAndUnrecognized_BucketsEachCorrectly()
    {
        var resolve = Resolver(
            ("Arclight Pistol", "Arclight Pistol"),
            ("Coda Pistol", "Coda Pistol"));
        var result = ScmdbImportPlan.Build(
            new[] { "Arclight Pistol", "Coda Pistol", "Nonexistent Test Widget" },
            new[] { "Coda Pistol" },
            resolve);

        Assert.Equal(new[] { "Arclight Pistol" }, result.ToImport);
        Assert.Equal(new[] { "Coda Pistol" }, result.AlreadyOwned);
        Assert.Equal(new[] { "Nonexistent Test Widget" }, result.Unrecognized);
    }

    [Fact]
    public void Build_TwoRawNamesResolvingToSameCanonical_CountedOnce()
    {
        // e.g. a stray duplicate export entry, or two SCMDB tags mapping to one Nexus blueprint.
        var resolve = Resolver(
            ("Arclight Pistol", "Arclight Pistol"),
            ("Arclight Pistol (dup)", "Arclight Pistol"));
        var result = ScmdbImportPlan.Build(
            new[] { "Arclight Pistol", "Arclight Pistol (dup)" }, Array.Empty<string>(), resolve);

        Assert.Equal(new[] { "Arclight Pistol" }, result.ToImport);
    }

    [Fact]
    public void Build_EmptyParsedNames_ProducesEmptyBuckets()
    {
        var result = ScmdbImportPlan.Build(Array.Empty<string>(), Array.Empty<string>(), Resolver());

        Assert.Empty(result.ToImport);
        Assert.Empty(result.AlreadyOwned);
        Assert.Empty(result.Unrecognized);
    }

    [Fact]
    public void Build_OwnedNameAbsentFromExport_NeverAppearsInAnyBucket()
    {
        // Add-only invariant: an owned blueprint the export simply doesn't mention is never
        // touched. The plan has no bucket or action that could ever un-own it.
        var resolve = Resolver(("Arclight Pistol", "Arclight Pistol"));
        var result = ScmdbImportPlan.Build(
            new[] { "Arclight Pistol" }, new[] { "Arclight Pistol", "Untouched Owned Item" }, resolve);

        Assert.DoesNotContain("Untouched Owned Item", result.ToImport);
        Assert.DoesNotContain("Untouched Owned Item", result.AlreadyOwned);
        Assert.DoesNotContain("Untouched Owned Item", result.Unrecognized);
    }
}
