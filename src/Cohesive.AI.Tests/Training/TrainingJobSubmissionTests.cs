using System.Text.Json;
using Cohesive.AI.Training;

namespace Cohesive.AI.Tests.Training;

public sealed class TrainingJobSubmissionTests
{
    [Fact]
    public void Constructor_SnapshotsDatasetsAndProducesVersionedFingerprint()
    {
        var datasets = new List<TrainingDatasetArtifact>
        {
            Dataset("validation", "https://artifacts.example/validation.parquet", rowCount: 20),
            Dataset("training", "https://artifacts.example/training.parquet", rowCount: 100)
        };
        var request = Request(datasets);

        var submission = new TrainingJobSubmission("training-run/42/submission", request);
        datasets.Clear();

        Assert.Equal(2, submission.Request.Datasets.Count);
        Assert.StartsWith($"{TrainingJobSubmission.RequestFingerprintAlgorithm}:", submission.RequestFingerprint);
        Assert.Equal(74, submission.RequestFingerprint.Length);
    }

    [Fact]
    public void Fingerprint_IsStableAcrossDatasetBindingOrder()
    {
        var training = Dataset("training", "https://artifacts.example/training.parquet", rowCount: 100);
        var validation = Dataset("validation", "https://artifacts.example/validation.parquet", rowCount: 20);

        var first = new TrainingJobSubmission("submission-a", Request([training, validation]));
        var second = new TrainingJobSubmission("submission-b", Request([validation, training]));

        Assert.Equal(first.RequestFingerprint, second.RequestFingerprint);
        Assert.Equal(
            "sha256-v1:56f27acfff35aca71a3e04c3e11e9f832d0d9896eab062ac0831eb30597d4be7",
            first.RequestFingerprint);
    }

    [Fact]
    public void Fingerprint_ChangesWhenExactRequestContentChanges()
    {
        var baseline = Request([Dataset("training", "https://artifacts.example/training.parquet", rowCount: 100)]);
        TrainingRequest[] variants =
        [
            baseline with { ModelName = "different-model" },
            baseline with { BaseVersion = null },
            baseline with { Datasets = [Dataset("training", "https://artifacts.example/other.parquet", rowCount: 100)] },
            baseline with { Code = new("https://artifacts.example/code.zip", "sha256:other") },
            baseline with { OutputModelName = "different-output" },
            baseline with { ExperimentName = null },
            baseline with { ComputeTarget = null },
            baseline with { ConfigJson = "{\"learningRate\":0.2}" }
        ];

        var baselineFingerprint = new TrainingJobSubmission("submission", baseline).RequestFingerprint;

        Assert.All(
            variants,
            variant => Assert.NotEqual(
                baselineFingerprint,
                new TrainingJobSubmission("submission", variant).RequestFingerprint));
    }

    [Fact]
    public void Constructor_RejectsDuplicateDatasetBindingNames()
    {
        var request = Request(
        [
            Dataset("training", "https://artifacts.example/first.parquet", rowCount: 10),
            Dataset("training", "https://artifacts.example/second.parquet", rowCount: 20)
        ]);

        var error = Assert.Throws<ArgumentException>(() => new TrainingJobSubmission("submission", request));

        Assert.Contains("duplicate dataset binding name 'training'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRoundTrip_RecomputesAndPreservesExactSubmissionEvidence()
    {
        var submission = new TrainingJobSubmission(
            "training-run/42/submission",
            Request([Dataset("training", "https://artifacts.example/training.parquet", rowCount: 100)]));

        var json = JsonSerializer.Serialize(submission);
        var roundTripped = JsonSerializer.Deserialize<TrainingJobSubmission>(json);

        Assert.DoesNotContain(nameof(TrainingJobSubmission.RequestFingerprint), json, StringComparison.Ordinal);
        Assert.NotNull(roundTripped);
        Assert.Equal(submission.SubmissionId, roundTripped.SubmissionId);
        Assert.Equal(submission.Request.ModelName, roundTripped.Request.ModelName);
        Assert.Equal(submission.Request.BaseVersion, roundTripped.Request.BaseVersion);
        Assert.Equal(submission.Request.Datasets, roundTripped.Request.Datasets);
        Assert.Equal(submission.Request.Code, roundTripped.Request.Code);
        Assert.Equal(submission.Request.OutputModelName, roundTripped.Request.OutputModelName);
        Assert.Equal(submission.Request.ExperimentName, roundTripped.Request.ExperimentName);
        Assert.Equal(submission.Request.ComputeTarget, roundTripped.Request.ComputeTarget);
        Assert.Equal(submission.Request.ConfigJson, roundTripped.Request.ConfigJson);
        Assert.Equal(submission.RequestFingerprint, roundTripped.RequestFingerprint);
    }

    static TrainingRequest Request(IReadOnlyList<TrainingDatasetArtifact> datasets) => new(
        ModelName: "semantic-matcher",
        BaseVersion: "v3",
        Datasets: datasets,
        Code: new("https://artifacts.example/code.zip", "sha256:code"),
        OutputModelName: "semantic-matcher-v4",
        ExperimentName: "nightly",
        ComputeTarget: "gpu-cluster",
        ConfigJson: "{\"learningRate\":0.1}");

    static TrainingDatasetArtifact Dataset(string name, string location, int rowCount) => new(
        Name: name,
        Location: location,
        Kind: TrainingDatasetArtifactKind.File,
        Format: "parquet",
        SchemaHash: "sha256:schema",
        RowCount: rowCount);
}
