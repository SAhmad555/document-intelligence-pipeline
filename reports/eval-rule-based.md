# Evaluation report - `rule-based`

Run 2026-11-23 10:25 UTC over **80 documents** (0 extraction failures).

## Headline

| Metric | Value |
|---|---|
| Field F1 (micro) | **80.7 %** |
| Documents fully correct | 42.5 % |
| Straight-through (auto-approved) | 23.7 % |
| Auto-approved but wrong | **21.1 %** |
| Printed arithmetic errors caught | 100 % |
| Latency p50 / p95 | 0 ms / 16 ms |

## Per field

| Field | Precision | Recall | F1 | Correct | Wrong | Missed | Spurious |
|---|---|---|---|---|---|---|---|
| invoiceNumber | 100.0 % | 62.5 % | 76.9 % | 50 | 0 | 30 | 0 |
| invoiceDate | 100.0 % | 58.8 % | 74.0 % | 47 | 0 | 33 | 0 |
| dueDate | 100.0 % | 59.7 % | 74.7 % | 37 | 0 | 25 | 0 |
| supplierName | 75.0 % | 75.0 % | 75.0 % | 60 | 20 | 0 | 0 |
| supplierVatNumber | 100.0 % | 78.6 % | 88.0 % | 33 | 0 | 9 | 0 |
| customerName | 100.0 % | 61.3 % | 76.0 % | 46 | 0 | 29 | 0 |
| currency | 100.0 % | 100.0 % | 100.0 % | 80 | 0 | 0 | 0 |
| subtotal | 100.0 % | 58.8 % | 74.0 % | 47 | 0 | 33 | 0 |
| taxAmount | 100.0 % | 75.0 % | 85.7 % | 60 | 0 | 20 | 0 |
| total | 100.0 % | 58.8 % | 74.0 % | 47 | 0 | 33 | 0 |
| lineItems | 100.0 % | 75.0 % | 85.7 % | 60 | 0 | 20 | 0 |

## Per layout

| Layout | Docs | Fully correct | Field accuracy | Auto-approved |
|---|---|---|---|---|
| classic | 20 | 90.0 % | 99.1 % | 0.0 % |
| email | 20 | 0.0 % | 20.0 % | 0.0 % |
| modern | 20 | 80.0 % | 98.2 % | 95.0 % |
| ocr-noisy | 20 | 0.0 % | 68.2 % | 0.0 % |

## Sample errors

- inv-0001 (classic): supplierVatNumber missed - expected 'IE5332508W', got 'null'
- inv-0002 (modern): supplierVatNumber missed - expected 'IE6504762W', got 'null'
- inv-0003 (email): invoiceNumber missed - expected 'A-10021', got 'null'
- inv-0003 (email): invoiceDate missed - expected '2026-04-23', got 'null'
- inv-0003 (email): dueDate missed - expected '2026-05-07', got 'null'
- inv-0003 (email): supplierName wrong - expected 'Granite Analytics Ltd', got 'Hi Kestrel IT Solutions Ltd accounts team,'
- inv-0003 (email): supplierVatNumber missed - expected 'IE1475429W', got 'null'
- inv-0003 (email): customerName missed - expected 'Kestrel IT Solutions Ltd', got 'null'
- inv-0003 (email): subtotal missed - expected '7582.65', got 'null'
- inv-0003 (email): taxAmount missed - expected '1516.53', got 'null'
- inv-0003 (email): total missed - expected '9099.18', got 'null'
- inv-0003 (email): lineItems missed - expected '3 lines', got 'null'
- inv-0004 (ocr-noisy): dueDate missed - expected '2026-08-30', got 'null'
- inv-0004 (ocr-noisy): subtotal missed - expected '10345.18', got 'null'
- inv-0007 (email): invoiceNumber missed - expected 'INV-2026-0007', got 'null'
- inv-0007 (email): invoiceDate missed - expected '2026-03-03', got 'null'
- inv-0007 (email): dueDate missed - expected '2026-03-17', got 'null'
- inv-0007 (email): supplierName wrong - expected 'Harbour Print Works Ltd', got 'Hi Kestrel Recruitment Limited accounts team,'
- inv-0007 (email): customerName missed - expected 'Kestrel Recruitment Limited', got 'null'
- inv-0007 (email): subtotal missed - expected '14092.14', got 'null'
- inv-0007 (email): taxAmount missed - expected '2818.43', got 'null'
- inv-0007 (email): total missed - expected '16910.57', got 'null'
- inv-0007 (email): lineItems missed - expected '3 lines', got 'null'
- inv-0008 (ocr-noisy): invoiceDate missed - expected '2026-09-20', got 'null'
- inv-0008 (ocr-noisy): subtotal missed - expected '22475.00', got 'null'
