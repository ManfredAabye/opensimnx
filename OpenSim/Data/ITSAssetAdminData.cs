using System;

namespace OpenSim.Data
{
    public struct TSAssetMoveReport
    {
        public string Source;
        public string Destination;
        public int CandidateCount;
        public int AlreadyInTargetCount;
        public int InsertedCount;
        public int DeletedFromSourceCount;
        public int IndexAffectedCount;
    }

    public struct TSAssetMoveOptions
    {
        public bool ResetCheckpoint;
        public int BatchSize;
        public int CommandTimeoutSeconds;
    }

    public struct TSAssetFindReport
    {
        public string AssetId;
        public bool Found;
        public string TableName;
        public bool HasIndexEntry;
        public int IndexAssetType;
    }

    public struct TSAssetVerifyReport
    {
        public string Scope;
        public int TablesChecked;
        public int TotalRows;
        public int MissingIndexRows;
        public int WrongIndexTypeRows;
        public int OrphanIndexRows;
        public int LegacyRowsWithIndex;
    }

    public struct TSAssetReindexReport
    {
        public string Scope;
        public int TablesProcessed;
        public int RowsScanned;
        public int IndexRowsUpserted;
        public int IndexRowsDeleted;
    }

    public interface ITSAssetAdminData
    {
        bool TryMoveAssets(string from, string to, out TSAssetMoveReport report, out string errorMessage);
        bool TryMoveAssets(string from, string to, TSAssetMoveOptions options, out TSAssetMoveReport report, out string errorMessage);
        bool TryPreviewMoveAssets(string from, string to, out TSAssetMoveReport report, out string errorMessage);
        bool TryFindAssetLocation(string assetId, out TSAssetFindReport report, out string errorMessage);
        bool TryVerifyAssets(string scope, out TSAssetVerifyReport report, out string errorMessage);
        bool TryReindexAssets(string scope, TSAssetMoveOptions options, out TSAssetReindexReport report, out string errorMessage);
        bool TryCleanLegacyIndex(TSAssetMoveOptions options, out TSAssetReindexReport report, out string errorMessage);
    }
}
