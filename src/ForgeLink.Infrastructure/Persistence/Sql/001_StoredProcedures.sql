-- ForgeLink SQL Server programmability. Idempotent (CREATE OR ALTER) so it can run on every deployment.
-- Batches are separated by GO; DatabaseInitializer splits on it.

-- Downtime Pareto for a line, clipping each event to the reporting window.
CREATE OR ALTER PROCEDURE dbo.usp_DowntimePareto
    @Line    NVARCHAR(20),
    @FromUtc DATETIME2,
    @ToUtc   DATETIME2
AS
BEGIN
    SET NOCOUNT ON;

    SELECT  d.ReasonCode,
            CAST(SUM(DATEDIFF(SECOND,
                    CASE WHEN d.StartUtc < @FromUtc THEN @FromUtc ELSE d.StartUtc END,
                    CASE WHEN d.EndUtc IS NULL OR d.EndUtc > @ToUtc THEN @ToUtc ELSE d.EndUtc END)) / 60.0 AS FLOAT) AS Minutes,
            COUNT(*) AS Occurrences
    FROM    dbo.DowntimeEvents AS d
    JOIN    dbo.Machines AS m ON m.Code = d.MachineCode
    WHERE   m.Line = @Line
      AND   d.StartUtc < @ToUtc
      AND  (d.EndUtc IS NULL OR d.EndUtc > @FromUtc)
    GROUP BY d.ReasonCode
    ORDER BY Minutes DESC;
END
GO

-- Housekeeping: purge delivered outbox rows and expired idempotency records in small batches
-- to avoid long locks on a 24x7 system. Schedule from SQL Agent / Azure Elastic Jobs.
CREATE OR ALTER PROCEDURE dbo.usp_PurgeIntegrationHistory
    @RetainDays INT = 30,
    @BatchSize  INT = 5000
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @Cutoff DATETIME2 = DATEADD(DAY, -@RetainDays, SYSUTCDATETIME());
    DECLARE @Rows INT = 1;

    WHILE @Rows > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.OutboxMessages
        WHERE Status = 'Sent' AND SentAtUtc < @Cutoff;
        SET @Rows = @@ROWCOUNT;
    END

    SET @Rows = 1;
    WHILE @Rows > 0
    BEGIN
        DELETE TOP (@BatchSize) FROM dbo.IdempotencyRecords WHERE CreatedAtUtc < @Cutoff;
        SET @Rows = @@ROWCOUNT;
    END
END
GO

-- Reconciliation: shop-floor inventory transactions with no matching ERP acknowledgement after @GraceMinutes.
-- Used by the nightly integrity check and by auditors.
CREATE OR ALTER PROCEDURE dbo.usp_UnacknowledgedTransactions
    @GraceMinutes INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    SELECT  t.Id, t.Type, t.Sku, t.LotNumber, t.Quantity, t.OccurredAtUtc, o.Status AS OutboxStatus, o.LastError
    FROM    dbo.InventoryTransactions AS t
    LEFT JOIN dbo.OutboxMessages AS o
           ON o.IdempotencyKey = REPLACE(CONVERT(NVARCHAR(36), t.Id), '-', '')
    WHERE   t.Type IN ('Move', 'Adjustment')
      AND   t.OccurredAtUtc < DATEADD(MINUTE, -@GraceMinutes, SYSUTCDATETIME())
      AND  (o.Id IS NULL OR o.Status <> 'Sent')
    ORDER BY t.OccurredAtUtc;
END
GO
