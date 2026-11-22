# Baseline persistence and encryption inventory
**Baseline**: source unchanged from `c44dc1b`, feature specification `48eeccd`. Static source inventory is a bounded starting point, not proof of runtime path completion. Follow actual callers and dynamic/file-format dependencies during T001. All rows require a final evidence/disposition entry before closure.
## Source files
Raw-command and encryption flags are lexical discovery signals, not assertions that every occurrence is target business SQL or encryption; e.g. source-only reads and authentication metadata must be classified by use. Review every current persistence file even without lexical matches.
| File | Raw command signal | Encryption signal | Required disposition |
| --- | --- | --- | --- |
| `src/Platform.Api/BrandEndpoints.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Api/CatalogEndpoints.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Api/OrderAndWalletEndpoints.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Api/SqliteStagingApi.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Api/SyntheticStaging.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.BotHost/StagingPreviewBot.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Fulfillment/Providers/AkundingSourceInspection.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Fulfillment/Providers/AkundingSupplierAdapter.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Fulfillment/Providers/ProdSellerSupplierAdapter.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.JobHost/SqliteStagingWorker.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Migration/HistoricalOrderImportWorkflow.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/ImportWorkflow.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyActivityReconciliation.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyAggregateComparison.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyArchiveFiles.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyCatalogImportWorkflow.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyCouponImportWorkflow.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyCustomerHistoryProjection.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyDeliveryImportWorkflow.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyFernetReader.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyMapStore.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyOrderSnapshotInspection.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyPaymentImportWorkflow.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyReadOnlyOrderHistory.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyRecordImport.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyRecordReconciliation.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacySourceCopyInspection.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacySourceReader.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacySourceRecords.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyWalletOrderPlan.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/LegacyWalletPlanning.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/OpeningBalanceImportWorkflow.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/ReadOnlyReconciliationRole.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/ReadOnlyReconciliationTarget.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/ReconciliationReport.cs` | yes | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/ReencryptionWorkflow.cs` | — | yes | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Migration/TargetActivityImportWorkflow.cs` | yes | — | Keep source-only readers; convert any target query/write to EF; isolate old decryptors in recovery tooling |
| `src/Platform.Payments/BankDestinationService.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Payments/ManualDepositService.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Payments/PaymentExpiryService.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AdminAuditStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AdminBindingStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AdminCatalogStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AdminCommand.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/AdminProductQuery.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/AdminRevocationStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AdminSessionStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AiContextStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/AssemblyInfo.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/BotActivityRetentionStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BotActivityStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BotActivityWriter.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BotConsumerOwnership.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BrandAdministrationStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BrandPolicyStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BrandUserStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/BrandWriterStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CampaignAudienceStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CampaignNoticeStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CampaignStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CatalogStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CheckoutPayments.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CheckoutStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CouponOwnerStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CustomerFlowExpiryStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/CustomerScreens.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/DbFailure.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/DbSession.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/DbStatement.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/DeliveryStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/HostedInvoiceStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/JobIntent.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/LocalPreorderStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/OperationsStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/OrderAdministrationStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/OutboxStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PaidSourceStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PaymentEvidenceStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PaymentInstructionStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PaymentMessageStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PaymentReadStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PaymentSettlementStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PreparedCheckoutStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/PricingStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/RestockNoticeStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SchemaManifest.cs` | — | — | Replace active target schema bootstrap with reviewed EF migration runner; preserve historical inputs |
| `src/Platform.Persistence/SchemaRunner.cs` | yes | — | Replace active target schema bootstrap with reviewed EF migration runner; preserve historical inputs |
| `src/Platform.Persistence/SqliteAdminCatalogStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteAdminCatalogWriteStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteAdminOrderStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteBankReviewStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteCatalogStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteCheckoutStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqliteCouponAdministrationStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteCouponDeliveryStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteCustomerScreens.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqliteDeliveryStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteDiscountClaimStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteIntegrity.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqliteInventoryImportStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteLocalPreorderStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteManualDepositStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteOrderPaymentSettlementStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteOutbox.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqlitePaymentCaptureStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqlitePaymentEvidenceStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqlitePaymentExpiryStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqlitePrepaidSourceStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqlitePreparedCheckoutStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteSchema.cs` | yes | — | Replace active target schema bootstrap with reviewed EF migration runner; preserve historical inputs |
| `src/Platform.Persistence/SqliteSession.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqliteStageHealthProbe.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteStageInventoryBridge.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteStagingConfiguration.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteStagingCopy.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqliteStagingIngress.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteStagingJobs.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteSupplierRequestStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteSupplierReviewStore.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Persistence/SqliteTopupSettlementStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/SqliteWallet.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SqliteWalletAdministrationStore.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.Persistence/StockSubscriptionStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SupplierRequestStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SupplierSourceSelectionStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/SupportStore.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/TelegramReplayStore.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/WriterBinding.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Persistence/WriterRegistry.cs` | — | — | Keep reusable types/utilities; adapt interfaces and error/ownership semantics to EF/plain content as needed |
| `src/Platform.SchemaRunner/ArchiveToolIdentity.cs` | — | yes | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/ReadOnlySourceArchiveCommand.cs` | — | yes | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/ReadOnlySourceArchiveVerification.cs` | yes | yes | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/ReadOnlyTargetFingerprints.cs` | yes | — | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/ReadOnlyTargetReconciliation.cs` | yes | yes | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/SharedWalletOrderQualification.cs` | yes | — | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/SourceCopyInspection.cs` | yes | — | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/WalletCopyQualification.cs` | yes | yes | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.SchemaRunner/WalletOrderCopyQualification.cs` | yes | yes | Explicit schema/recovery tooling only; reviewed DDL/source-read exceptions; target writes use EF |
| `src/Platform.Security/BotTokenVault.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Security/BrandCipher.cs` | — | yes | Remove from target runtime; isolate required old-target decoder in recovery tooling |
| `src/Platform.Security/CipherEnvelope.cs` | — | yes | Remove from target runtime; isolate required old-target decoder in recovery tooling |
| `src/Platform.Security/CustomerHistoryArchive.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Security/CustomerHistoryFiles.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Security/InventoryCipher.cs` | — | yes | Remove from target runtime; isolate required old-target decoder in recovery tooling |
| `src/Platform.Security/SecretFiles.cs` | — | yes | Remove target encryption/keyring coupling; retain non-encryption authentication and access protections |
| `src/Platform.Telegram/CustomerHistoricalTypedAliasReader.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Telegram/HumanSupportRouter.cs` | yes | yes | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Telegram/UpdateRouter.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
| `src/Platform.Telegram/WalletCheckoutService.cs` | yes | — | Convert target queries/writes to EF and keep existing caller contracts |
## Non-source surfaces
| Path | Required review |
| --- | --- |
| `ops/migrations/0001_core.sql`, `ops/migrations/sqlite/0001_core.sql` | Preserve pinned baselines; inventory all modeled columns/checks/indexes/triggers/functions for new EF migrations |
| `ops/staging/` | Synthetic provisioning, bot inbox/history/evidence formats, mounted secrets, inventory bridge, key manifests, schema/version checks, rollback and publish-run probes |
| `.github/workflows/backend-ci.yml` | Update prospective target checks/artifact format only; retain historical evidence; suppress deployment during qualification |
| `tests/Platform.*Tests/` | Preserve relevant regression evidence; move legacy decryptor tests to tooling scope and replace target encryption assertions with plaintext/auth/roundtrip/failure tests |
| `specs/001-multibrand-redesign/`, `specs/003-ef-core-persistence/`, `specs/004-sqlite-persistence/`, `ops/evidence/` | Historical approvals/checklists/hash commitments remain unchanged; 005 prospectively supersedes only its explicit behavior |
## Closure register
For each source/non-source row record final target path, target/source/historical classification, removed or converted behavior, affected FR/SC, exact candidate revision and validation evidence. Mark retained non-business SQL with the exception ID in contracts/persistence.md. No unmapped target write, runtime encrypt/decrypt call or undeclared exception may remain. A grep count alone cannot close this register.
