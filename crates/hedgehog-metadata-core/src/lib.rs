pub mod command {
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct CreateWriteIntent;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct CompleteReplica;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct CommitVersion;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct DeleteObject;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct LeaseRepair;
}

pub mod decision {
    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct Decision;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct RowPatch;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct OutboxIntent;

    #[derive(Debug, Clone, PartialEq, Eq)]
    pub struct AuditIntent;
}

