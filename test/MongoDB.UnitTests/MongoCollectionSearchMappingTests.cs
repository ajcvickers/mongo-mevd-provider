// Copyright (c) Microsoft. All rights reserved.

using System.Linq;
using MongoDB.Bson;
using MongoDB.VectorData;
using Xunit;

namespace MongoDB.VectorData.UnitTests;

/// <summary>
/// Unit tests for <see cref="MongoCollectionSearchMapping"/> pipeline construction.
/// </summary>
public sealed class MongoCollectionSearchMappingTests
{
    [Fact]
    public void GetHybridSearchPipelineWeightsFullTextSearchHigherThanVectorSearch()
    {
        // MEVD's IKeywordHybridSearchable treats hybrid search as keyword-primary with vector
        // re-ranking. The upstream HybridSearchTests<TKey> build all test records with an
        // identical vector and assert that the keyword match wins (e.g. HybridSearchAsync_with_top,
        // HybridSearchAsync_with_Skip) — that only holds if the FTS branch's per-rank weight
        // dominates the vector branch's. The weights here pin that contract.
        var pipeline = MongoCollectionSearchMapping.GetHybridSearchPipeline(
            new[] { 0.1f, 0.2f, 0.3f },
            keywords: new[] { "alpha", "beta" },
            collectionName: "hotels",
            vectorIndexName: "vector_index",
            fullTextSearchIndexName: "full_text_search_index",
            vectorPropertyName: "embedding",
            textPropertyName: "description",
            scorePropertyName: "score",
            documentPropertyName: "document",
            limit: 10,
            numCandidates: 100,
            filter: null);

        // The vector-search branch adds the rank-weighted vs_score directly on the outer pipeline,
        // before the $unionWith with the FTS branch.
        var vsWeight = ExtractAddScoreWeight(pipeline, "vs_score");
        var ftsBranch = pipeline.Single(d => d.Contains("$unionWith"))["$unionWith"]["pipeline"].AsBsonArray.Cast<BsonDocument>();
        var ftsWeight = ExtractAddScoreWeight(ftsBranch, "fts_score");

        Assert.Equal(0.1, vsWeight);
        Assert.Equal(0.9, ftsWeight);
    }

    private static double ExtractAddScoreWeight(System.Collections.Generic.IEnumerable<BsonDocument> stages, string scoreField)
    {
        var addFields = stages.Single(d =>
            d.Contains("$addFields") && d["$addFields"].AsBsonDocument.Contains(scoreField));

        // $addFields: { <scoreField>: { $multiply: [ <weight>, { $divide: [1.0, { $add: ["$rank", 60] }] } ] } }
        var multiply = addFields["$addFields"][scoreField]["$multiply"].AsBsonArray;
        return multiply[0].ToDouble();
    }
    
    [Fact]
    public void HybridSearchPipelineAppliesFullTextFilterAsMatchStageNotInsideSearch()
    {
        // The full-text $search stage cannot take an MQL filter, so the hybrid pipeline applies the filter as a
        // $match immediately after $search (the vector branch, in contrast, embeds it in $vectorSearch.filter).
        // This locks that behavior so the (unused) filter parameter can be removed from GetFullTextSearchQuery
        // without changing what the pipeline does.
        var filter = new BsonDocument { ["category"] = "books" };
        float[] vector = [1f, 2f, 3f, 4f];

        var pipeline = MongoCollectionSearchMapping.GetHybridSearchPipeline(
            vector,
            ["term"],
            collectionName: "coll",
            vectorIndexName: "vector_index",
            fullTextSearchIndexName: "fts_index",
            vectorPropertyName: "embedding",
            textPropertyName: "text",
            scorePropertyName: "similarityScore",
            documentPropertyName: "document",
            limit: 10,
            numCandidates: 100,
            filter);

        // The full-text branch lives inside the $unionWith stage.
        var ftsPipeline = pipeline.Single(stage => stage.Contains("$unionWith"))["$unionWith"]["pipeline"].AsBsonArray;

        var searchStage = ftsPipeline.Single(s => s.AsBsonDocument.Contains("$search"))["$search"].AsBsonDocument;
        var matchStage = ftsPipeline.Single(s => s.AsBsonDocument.Contains("$match"))["$match"].AsBsonDocument;

        Assert.False(searchStage.Contains("filter"), "The full-text $search stage must not embed the MQL filter.");
        Assert.Equal(filter, matchStage);

        // The vector branch keeps embedding the filter in $vectorSearch.filter.
        var vectorSearchStage = pipeline.Single(stage => stage.Contains("$vectorSearch"))["$vectorSearch"].AsBsonDocument;
        Assert.Equal(filter, vectorSearchStage["filter"].AsBsonDocument);
    }
}
