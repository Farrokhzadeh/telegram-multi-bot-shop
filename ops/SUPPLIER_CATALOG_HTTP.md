# Optional supplier catalog administrator API

This extension exposes the existing Akunding catalog-selection backend for an explicitly composed, independently authenticated administrator host. Register MapAkundingSupplierAdministration with the protected AkundingSourceInspection and SupplierSourceSelectionStore. Install the existing routing, DiagnosticsMiddleware and AdminGuardMiddleware with the database-backed current-session authenticator. Composition owns and disposes the inspection and credential snapshots. Match credential deployment/service/account to that host's commerce snapshot.

Normal hosts do not register this extension. The frozen 29-method core OpenAPI contract is unchanged; these four methods form a separate optional surface. No dashboard, deployment, live supplier activation, schema change, mapping enablement, source switching or purchasing is introduced.

## Routes

All paths are under /v1/admin/products/{product}, where product is the existing shared shop product UUID. All methods require Authorization: Bearer with an independent administrator session. Bot identity, administrator-looking headers and supplier keys grant no administrator authority.

| Method | Path suffix | Query or body | Successful result |
|---|---|---|---|
| GET | /suppliers/akunding/catalog | Required productRevision; optional search (80 characters), after (positive numeric ID), limit (1–100, default 50) | AkundingCatalogPage: Items and nullable Next |
| GET | /supplier-sources | Optional after (source UUID), limit (1–100, default 50) | Existing AdminPage of SupplierSourceMetadata |
| POST | /suppliers/akunding/sources | Strict AkundingSourceChoice JSON below | AkundingSourcePreview for one disabled candidate |
| GET | /suppliers/akunding/sources/{source} | Required sourceRevision | AkundingSourcePreview for the exact existing mapping |

Catalog pages include Id, Name, UnitLabel, UnitPrice, Stock, Available and Currency (USDT). The after cursor is exclusive and each request freshly reads the bounded catalog. Saved source metadata includes Id, Provider, Product (external ID), Variant, Enabled, Revision and Selected; it contains no key or credential content. Successful handler responses use Cache-Control: no-store.

POST body property names are case-sensitive. All four fields are required:

~~~json
{
  "ProductRevision": 2,
  "ExternalProductId": 7,
  "SourceId": "11111111-1111-4111-8111-111111111111",
  "CommandId": "22222222-2222-4222-8222-222222222222"
}
~~~

The UUIDs and numeric values above are synthetic examples. Generate source/command IDs once per intended mapping and retain them for a retry. The route supplies the shop product identity; the body cannot override it, the configured account, provider origin or authentication. Duplicate and unknown JSON properties, missing constructor fields, invalid IDs/revisions and wrong content types are rejected. Request bodies are bounded by the existing 1 MiB administrator reader.

## Selection flow and limits

Read the shop product's current revision through existing administrator metadata, browse/search the supplier catalog, then POST exactly the chosen item. POST performs a fresh product detail read and checks current authority/product revision before and after transport; the final audited insert rechecks the product revision under lock. It creates only a disabled, unobserved candidate, with no active-source pointer or publication changes. Listing saved sources and previewing one mapping lets a future dashboard show the result. An identical committed command does not add another mapping; changed identity/revision cannot reuse the command. Catalog browsing itself writes no mappings.

Responses do not establish ready-item eligibility, a payable quote or an approved local price guard. Sold-out items can be saved as disabled candidates. The owner chooses credential/key/code products individually; unrelated supplier products are not automatically imported or published. Enablement, active-source selection, brand publication, supplier purchasing/recovery and runtime deployment retain their existing requirements. T057/T091 remain open.

Errors use the existing correlated ApiFailure shape: code and requestId, matching X-Request-Id. Invalid requests return 400, unsupported body content type 415, unauthenticated/revoked sessions 401, and stale revisions, mapping conflicts or sanitized catalog failures 409. Error bodies contain no supplier response, token or purchased credentials. Authentication runs before binding/business access; current authority is checked again during the operation.

HTTP qualification uses only loopback hosts, generated credentials, synthetic supplier responses and disposable databases. Tests cover all four route guards, successful search/pagination/selection/list/preview, strict JSON and query/body boundaries, replay, concurrent revocation/product edits, failed supplier reads and zero publication/procurement effects. Full CI also runs the existing frozen-core and Native AOT regression checks; this does not qualify a deployed/live provider.
