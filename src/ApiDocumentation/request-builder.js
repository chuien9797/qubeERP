(() => {
    const service = new URL(document.currentScript.src).searchParams.get("service");
    const recent = { orders: [], reservations: [] };
    let catalogue;
    let catalogueRequest;

    async function loadProducts(refresh = false) {
        if (refresh) {
            catalogue = undefined;
            catalogueRequest = undefined;
        }
        if (catalogue) return catalogue;
        if (!catalogueRequest) {
            catalogueRequest = (async () => {
                const products = [];
                let page;
                do {
                    const url = new URL(service === "Products" ? "../api/products" : "products", location.href);
                    url.searchParams.set("offset", String(products.length));
                    url.searchParams.set("limit", "200");
                    const response = await fetch(url, { signal: AbortSignal.timeout(8000) });
                    if (!response.ok) throw new Error("Product list is unavailable. Start ProductService and refresh the list.");
                    page = await response.json();
                    if (!Array.isArray(page)) throw new Error("The product list could not be read.");
                    products.push(...page);
                } while (page.length === 200);
                catalogue = products.sort((left, right) => left.sku.localeCompare(right.sku));
                return catalogue;
            })().finally(() => { catalogueRequest = undefined; });
        }
        return catalogueRequest;
    }

    function parseBody(value) {
        try {
            const parsed = typeof value === "string" ? JSON.parse(value) : value;
            return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
        } catch {
            return {};
        }
    }

    function readDraft(kind) {
        try {
            return sessionStorage.getItem(`qube.swagger.${kind}`);
        } catch {
            return null;
        }
    }

    function saveDraft(kind, value) {
        try {
            sessionStorage.setItem(`qube.swagger.${kind}`, value);
        } catch {
            return;
        }
    }

    window.QubeSwaggerPlugin = system => {
        const React = system.React;
        const h = React.createElement;
        const help = text => h("p", { className: "qube-help" }, text);
        const button = (text, onClick, disabled = false) => h("button", { type: "button", className: "btn", onClick, disabled }, text);

        function useCatalogue() {
            const [state, setState] = React.useState({ products: catalogue || [], loading: !catalogue, error: "" });
            React.useEffect(() => {
                let active = true;
                loadProducts().then(products => {
                    if (active) setState({ products, loading: false, error: "" });
                }).catch(() => {
                    if (active) setState(previous => ({ ...previous, loading: false, error: "Product list is unavailable. Start ProductService and refresh the list." }));
                });
                return () => { active = false; };
            }, []);
            const refresh = async () => {
                setState(previous => ({ ...previous, loading: true, error: "" }));
                try {
                    const products = await loadProducts(true);
                    setState({ products, loading: false, error: "" });
                } catch {
                    setState(previous => ({ ...previous, loading: false, error: "Product list is unavailable. Start ProductService and refresh the list." }));
                }
            };
            return { ...state, refresh };
        }

        function ProductSelect({ products, value, onChange, selected = [], activeOnly = false, showPrice = false, label = "Product" }) {
            const known = products.some(product => product.id === value);
            return h("select", { value: value || "", onChange: event => onChange(event.target.value), "aria-label": label, required: true },
                h("option", { value: "" }, "Choose a product"),
                value && !known ? h("option", { value }, `Saved selection (${value})`) : null,
                products.map(product => h("option", {
                    key: product.id,
                    value: product.id,
                    disabled: (activeOnly && !product.isActive) || (selected.includes(product.id) && product.id !== value)
                }, `${product.name} (${product.sku})${showPrice ? ` — ${Number(product.unitPrice).toFixed(2)}` : ""}${product.isActive ? "" : " — inactive"}`)));
        }

        function CatalogueStatus({ state }) {
            return h("div", null,
                state.loading ? help("Loading products…") : null,
                state.error ? h("p", { role: "alert", className: "qube-help qube-error" }, state.error) : null,
                !state.loading && !state.error && !state.products.length ? help("No products yet. Create a product in ProductService, then refresh this list.") : null,
                button("Refresh products", state.refresh, state.loading));
        }

        function Field({ label, value, onChange, type = "text", ...attributes }) {
            const id = React.useId();
            return h("div", null,
                h("label", { htmlFor: id }, label),
                h("input", { id, type, value: value ?? "", onChange: event => onChange(event.target.value), ...attributes }));
        }

        function ProductLines({ body, kind, update }) {
            const state = useCatalogue();
            const lookup = kind === "lookup";
            const items = lookup
                ? (Array.isArray(body.productIds) ? body.productIds : []).map(productId => ({ productId: typeof productId === "string" ? productId : "" }))
                : (Array.isArray(body.items) ? body.items : []).map(item => item && typeof item === "object" ? item : { productId: "", quantity: 1 });
            const rows = items.length ? items : [{ productId: "", quantity: 1 }];
            const save = next => update(lookup ? { productIds: next.map(item => item.productId) } : { items: next });
            const edit = (index, change) => save(rows.map((item, position) => position === index ? { ...item, ...change } : item));
            return h("div", null,
                h(CatalogueStatus, { state }),
                rows.map((item, index) => h("div", { key: index, className: "qube-line" },
                    h("div", null,
                        h("label", null, `Product ${index + 1}`),
                        h(ProductSelect, {
                            products: state.products, value: item.productId,
                            selected: rows.map(row => row.productId), activeOnly: kind === "order", showPrice: kind === "order",
                            label: `Product ${index + 1}`,
                            onChange: productId => edit(index, { productId })
                        })),
                    !lookup ? h(Field, {
                        label: `Quantity ${index + 1}`, type: "number", min: 1, max: 10000, step: 1,
                        value: item.quantity,
                        onChange: quantity => edit(index, { quantity: quantity === "" ? null : Number(quantity) })
                    }) : h("span"),
                    button(`Remove product ${index + 1}`, () => save(rows.filter((_, position) => position !== index)), rows.length === 1))),
                button("Add product", () => save([...rows, { productId: "", quantity: 1 }]), rows.length >= 50),
                help(kind === "order" ? "Choose active products and quantities. Prices are loaded from the catalogue when you submit the order." : lookup ? "Choose up to 50 different products." : "Choose products with initialised stock. All quantities are reserved together, or none are reserved."));
        }

        function RequestForm({ Original, kind, ...props }) {
            const [raw, setRaw] = React.useState(false);
            const persistent = ["reservation", "order", "receipt"].includes(kind);
            const body = parseBody(props.requestBodyValue || (persistent ? readDraft(kind) : null));
            const [generatedId, setGeneratedId] = React.useState(() => body.reservationId || crypto.randomUUID());
            const [receiptId, setReceiptId] = React.useState(() => body.operationId || crypto.randomUUID());
            React.useEffect(() => {
                if (kind === "receipt" && body.operationId) setReceiptId(body.operationId);
            }, [body.operationId]);
            React.useEffect(() => {
                if (kind === "reservation" && body.reservationId) setGeneratedId(body.reservationId);
            }, [body.reservationId]);
            const changeBody = value => {
                if (persistent) saveDraft(kind, value);
                props.onChange(value);
            };
            const update = changes => changeBody(JSON.stringify({
                ...body,
                ...(kind === "reservation" ? { reservationId: body.reservationId || generatedId } : {}),
                ...(kind === "receipt" ? { operationId: body.operationId || receiptId } : {}),
                ...changes
            }, null, 2));
            React.useEffect(() => {
                if (kind === "reservation") {
                    changeBody(JSON.stringify({ ...body, reservationId: body.reservationId || generatedId, items: body.items || [] }, null, 2));
                } else if (kind === "receipt") {
                    changeBody(JSON.stringify({ ...body, operationId: body.operationId || receiptId, quantity: body.quantity ?? null, reason: body.reason || "" }, null, 2));
                } else if (!props.requestBodyValue) {
                    const initial = kind === "product" ? { sku: "", name: "", description: "", unitPrice: null }
                        : kind === "stock" ? { onHand: null, reason: "" }
                        : kind === "lookup" ? { productIds: [] } : { items: [] };
                    changeBody(JSON.stringify(persistent && readDraft(kind) ? body : initial, null, 2));
                }
            }, []);
            return h("div", { className: "qube-form" },
                button(raw ? "Use form" : "Edit JSON", () => {
                    if (raw && kind === "reservation" && !body.reservationId) update({ reservationId: generatedId });
                    if (raw && kind === "receipt" && !body.operationId) update({ operationId: receiptId });
                    setRaw(!raw);
                }),
                raw ? h(Original, { ...props, onChange: changeBody }) : h(React.Fragment, null,
                    kind === "product" ? h(React.Fragment, null,
                        h(Field, { label: "SKU", value: body.sku, required: true, maxLength: 50, placeholder: "MOUSE-01", onChange: sku => update({ sku }) }),
                        help("Choose a unique SKU. Leading/trailing spaces are removed and letters are saved in uppercase."),
                        h(Field, { label: "Product name", value: body.name, required: true, maxLength: 200, onChange: name => update({ name }) }),
                        h(Field, { label: "Description (optional)", value: body.description, onChange: description => update({ description }) }),
                        h(Field, { label: "Unit price", type: "number", min: 0, step: "0.01", required: true, value: body.unitPrice, onChange: price => update({ unitPrice: price === "" ? null : Number(price) }) }),
                        help("Enter a nonnegative price with at most two decimal places. The product ID is generated when the product is created.")) : null,
                    kind === "receipt" ? h(React.Fragment, null,
                        h(Field, { label: "Receipt ID (generated)", value: body.operationId || receiptId, readOnly: true }),
                        button("New receipt", () => update({ operationId: crypto.randomUUID() })),
                        help("Keep this ID and the same product, quantity and reason to retry a delivery safely. Choose New receipt only for a separate delivery. This draft is kept in this browser tab across reloads."),
                        h(Field, { label: "Quantity received", type: "number", min: 1, max: 2147483647, step: 1, required: true, value: body.quantity, onChange: quantity => update({ quantity: quantity === "" ? null : Number(quantity) }) }),
                        h(Field, { label: "Reason", value: body.reason, required: true, maxLength: 300, placeholder: "Supplier delivery reference", onChange: reason => update({ reason }) }),
                        help("Adds this quantity to existing physical stock. Reserved is unchanged. Initialise inventory with Set physical stock count first. Replays return the original receipt balances; GET inventory shows the current balance.")) : null,
                    kind === "stock" ? h(React.Fragment, null,
                        h(Field, { label: "Physical stock count", type: "number", min: 0, max: 2147483647, step: 1, required: true, value: body.onHand, onChange: count => update({ onHand: count === "" ? null : Number(count) }) }),
                        h(Field, { label: "Reason", value: body.reason, required: true, maxLength: 300, placeholder: "Stock count or replenishment", onChange: reason => update({ reason }) }),
                        help("This sets the total physical quantity; it does not add to the previous count. It cannot be lower than reserved stock.")) : null,
                    kind === "reservation" ? h(React.Fragment, null,
                        h(Field, { label: "Reservation ID (generated)", value: body.reservationId || generatedId, readOnly: true }),
                        button("New reservation", () => update({ reservationId: crypto.randomUUID() })),
                        help("The ID is generated automatically and this draft is kept in this browser tab across reloads. Execute again to retry the same reservation. Choose New reservation only when starting a separate stock hold.")) : null,
                    ["order", "reservation", "lookup"].includes(kind) ? h(ProductLines, { body, kind, update }) : null,
                    help("Complete the fields, then click Execute below.")));
        }

        function ProductParameter({ Original, ...props }) {
            const state = useCatalogue();
            const value = props.param.get("value") || "";
            const receipt = props.pathMethod[0] === "/api/inventory/{productId}/receipts";
            const change = id => {
                if (receipt) saveDraft("receipt-product", id);
                props.onChange(props.rawParam, id);
            };
            React.useEffect(() => {
                if (receipt && !value && readDraft("receipt-product")) change(readDraft("receipt-product"));
            }, []);
            return h("tr", { className: "qube-parameter" },
                h("td", { className: "parameters-col_name" }, h("strong", null, "Product")),
                h("td", { className: "parameters-col_description" },
                    h(ProductSelect, { products: state.products, value, onChange: change }),
                    h(CatalogueStatus, { state })));
        }

        function GeneratedKey(props) {
            const value = props.param.get("value") || "";
            const changeKey = key => {
                saveDraft("order-key", key);
                props.onChange(props.rawParam, key);
            };
            React.useEffect(() => {
                if (!value) changeKey(readDraft("order-key") || crypto.randomUUID());
            }, []);
            return h("tr", { className: "qube-parameter" },
                h("td", { className: "parameters-col_name" }, h("strong", null, "Request key (generated)")),
                h("td", { className: "parameters-col_description" },
                    h("input", { value, readOnly: true, "aria-label": "Order request key" }),
                    button("New order", () => changeKey(crypto.randomUUID())),
                    help("This key and the order draft are kept in this browser tab across reloads. Keep the key when retrying the same order, including a Pending order. Choose New order for a different purchase. The order ID is generated by OrderService.")));
        }

        function RecentParameter({ kind, ...props }) {
            const [, redraw] = React.useState(0);
            React.useEffect(() => {
                const changed = () => redraw(value => value + 1);
                window.addEventListener("qube-recent-changed", changed);
                return () => window.removeEventListener("qube-recent-changed", changed);
            }, []);
            const value = props.param.get("value") || "";
            const label = kind === "orders" ? "Order" : "Reservation";
            return h("tr", { className: "qube-parameter" },
                h("td", { className: "parameters-col_name" }, h("strong", null, label)),
                h("td", { className: "parameters-col_description" },
                    h("select", { value: recent[kind].some(item => item.id === value) ? value : "", "aria-label": `Recent ${label.toLowerCase()}`, onChange: event => props.onChange(props.rawParam, event.target.value) },
                        h("option", { value: "" }, `Choose a recent ${label.toLowerCase()}`),
                        recent[kind].map(item => h("option", { key: item.id, value: item.id }, `${item.id} — ${item.status}`))),
                    h("input", { value, placeholder: `Or paste a known ${label.toLowerCase()} ID`, "aria-label": `${label} ID`, onChange: event => props.onChange(props.rawParam, event.target.value) }),
                    help("Recent results from this Swagger page appear here after you execute a request. A page reload clears this list.")));
        }

        return {
            wrapComponents: {
                RequestBody: Original => props => {
                    const path = props.specPath.get(1);
                    const method = props.specPath.get(2);
                    const kind = path === "/api/products" ? "product"
                        : path === "/api/products/lookup" ? "lookup"
                        : path === "/api/inventory/{productId}" ? "stock"
                        : path === "/api/inventory/{productId}/receipts" ? "receipt"
                        : path === "/api/inventory/reservations" ? "reservation"
                        : path === "/api/orders" ? "order" : null;
                    return props.isExecute && kind && ["post", "put"].includes(method)
                        ? h(RequestForm, { ...props, Original, kind }) : h(Original, props);
                },
                parameterRow: Original => props => {
                    if (!props.isExecute) return h(Original, props);
                    const name = props.rawParam.get("name");
                    const path = props.pathMethod[0];
                    if (name === "Idempotency-Key") return h(GeneratedKey, props);
                    if (name === "productId" || (name === "id" && path === "/api/products/{id}")) return h(ProductParameter, { ...props, Original });
                    if (name === "reservationId") return h(RecentParameter, { ...props, kind: "reservations" });
                    if (name === "id" && path === "/api/orders/{id}") return h(RecentParameter, { ...props, kind: "orders" });
                    return h(Original, props);
                }
            },
            statePlugins: {
                spec: {
                    wrapActions: {
                        setResponse: original => (path, method, response) => {
                            const result = original(path, method, response);
                            const body = parseBody(response.obj || response.text);
                            const kind = body.orderId ? "orders" : body.reservationId ? "reservations" : null;
                            if (kind) {
                                const id = body.orderId || body.reservationId;
                                recent[kind] = [{ id, status: body.orderStatus || body.status || response.status }, ...recent[kind].filter(item => item.id !== id)].slice(0, 20);
                                window.dispatchEvent(new Event("qube-recent-changed"));
                            }
                            if (path === "/api/products" && method === "post" && response.status === 201) catalogue = undefined;
                            return result;
                        }
                    }
                }
            }
        };
    };
})();
