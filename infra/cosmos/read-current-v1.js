function readCurrentV1(id, partition) {
    if (typeof id !== "string" || !id.length || id.length > 1024 ||
        typeof partition !== "string" || !partition.length || partition.length > 2048) {
        throw new Error("Invalid primary point-read arguments.");
    }
    var context = getContext();
    var collection = context.getCollection();
    var query = { query: "SELECT TOP 1 * FROM c WHERE c.id = @id", parameters: [{ name: "@id", value: id }] };
    var accepted = collection.queryDocuments(collection.getSelfLink(), query, { pageSize: 1 }, function (error, documents, response) {
        if (error) throw error;
        if (!Array.isArray(documents) || documents.length > 1) throw new Error("Invalid primary lookup result.");
        if (documents.length === 0) {
            if (response && response.continuation) throw new Error("Primary lookup requires continuation; absence is not proven.");
            context.getResponse().setBody({ version: 1, found: false, document: null, etag: null });
            return;
        }
        var document = documents[0];
        if (!document || document.id !== id || document.pk !== partition || !document._etag) {
            throw new Error("Primary point-read identity or version mismatch.");
        }
        context.getResponse().setBody({ version: 1, found: true, document: document, etag: document._etag });
    });
    if (!accepted) throw new Error("Primary point-read was not accepted within the execution budget.");
}
