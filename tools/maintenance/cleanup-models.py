"""Deduplicate Models table in Azurite — keep one per WebLlmModelId."""
from collections import defaultdict
from azure.data.tables import TableServiceClient

CONN = "UseDevelopmentStorage=true"

svc = TableServiceClient.from_connection_string(CONN)
tbl = svc.get_table_client("Models")

groups = defaultdict(list)
for entity in tbl.list_entities():
    wid = entity.get("WebLlmModelId", "")
    groups[wid].append(entity)

deleted = 0
for wid, entities in groups.items():
    entities.sort(key=lambda x: x["RowKey"])  # ULID order = insertion order
    for dup in entities[1:]:
        tbl.delete_entity(partition_key=dup["PartitionKey"], row_key=dup["RowKey"])
        deleted += 1
        name = dup.get("DisplayName", "?")
        print(f"  Deleted: {name} ({dup['RowKey']})")

print(f"\nDeleted {deleted} duplicates.")
remaining = list(tbl.list_entities())
print(f"Remaining: {len(remaining)} models")
for e in remaining:
    print(f"  {e.get('DisplayName')} | {e.get('WebLlmModelId')}")