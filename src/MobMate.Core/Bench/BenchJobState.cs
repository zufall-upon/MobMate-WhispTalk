using System;
using System.Collections.Generic;
using System.Threading;

namespace MobMate.Core.Bench;

/// <summary>
/// In-memory benchmark job state. Thread-safe, no persistence.
/// </summary>
public sealed class BenchJobState
{
    public string JobId { get; }
    public string State { get; private set; } = "idle";
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public int Total { get; set; }
    public int Completed { get; set; }
    public int ExactCount { get; set; }
    public double? AvgCer { get; set; }
    public string? Error { get; private set; }
    public string? ResultPath { get; set; }

    private readonly ReaderWriterLockSlim _lock = new();

    public BenchJobState()
    {
        JobId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
    }

    public void Start(int total)
    {
        _lock.EnterWriteLock();
        try
        {
            State = "running";
            StartedAt = DateTimeOffset.UtcNow;
            Total = total;
            Completed = 0;
            ExactCount = 0;
            AvgCer = null;
            Error = null;
            ResultPath = null;
            FinishedAt = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Advance()
    {
        _lock.EnterWriteLock();
        try
        {
            Completed++;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Complete(string resultPath)
    {
        _lock.EnterWriteLock();
        try
        {
            State = "completed";
            FinishedAt = DateTimeOffset.UtcNow;
            ResultPath = resultPath;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void Fail(string error)
    {
        _lock.EnterWriteLock();
        try
        {
            State = "failed";
            FinishedAt = DateTimeOffset.UtcNow;
            Error = error;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Snapshot for serialization. Thread-safe read.
    /// </summary>
    public BenchJobStateSnapshot Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return new BenchJobStateSnapshot(
                JobId, State, StartedAt, FinishedAt,
                Total, Completed, ExactCount, AvgCer, Error, ResultPath);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}

/// <summary>
/// Immutable snapshot of bench job state for JSON serialization.
/// </summary>
public sealed class BenchJobStateSnapshot
{
    public string JobId { get; }
    public string State { get; }
    public DateTimeOffset? StartedAt { get; }
    public DateTimeOffset? FinishedAt { get; }
    public int Total { get; }
    public int Completed { get; }
    public int ExactCount { get; }
    public double? AvgCer { get; }
    public string? Error { get; }
    public string? ResultPath { get; }

    public BenchJobStateSnapshot(
        string jobId, string state, DateTimeOffset? startedAt, DateTimeOffset? finishedAt,
        int total, int completed, int exactCount, double? avgCer, string? error, string? resultPath)
    {
        JobId = jobId;
        State = state;
        StartedAt = startedAt;
        FinishedAt = finishedAt;
        Total = total;
        Completed = completed;
        ExactCount = exactCount;
        AvgCer = avgCer;
        Error = error;
        ResultPath = resultPath;
    }
}
