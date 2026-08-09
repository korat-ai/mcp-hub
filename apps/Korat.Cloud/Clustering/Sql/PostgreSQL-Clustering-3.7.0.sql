-- Orleans clustering migration 3.7.0 for PostgreSQL: adds the CleanupDefunctSiloEntries
-- query, which the Orleans 9.x runtime REQUIRES but the base PostgreSQL-Clustering.sql omits
-- (the base script ships only 8 of the required queries). Without this the silo fails to
-- start: "Not all required queries found. Missing are: CleanupDefunctSiloEntriesKey".
--
-- Made idempotent with ON CONFLICT (the upstream migration is a plain INSERT) so it can be
-- applied to a fresh schema OR back-filled onto an already-created base schema.
INSERT INTO OrleansQuery(QueryKey, QueryText)
VALUES
(
    'CleanupDefunctSiloEntriesKey','
    DELETE FROM OrleansMembershipTable
    WHERE DeploymentId = @DeploymentId
        AND @DeploymentId IS NOT NULL
        AND IAmAliveTime < @IAmAliveTime
        AND Status != 3;
')
ON CONFLICT (QueryKey) DO UPDATE SET QueryText = EXCLUDED.QueryText;
