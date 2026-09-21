# Privacy

KeelMatrix.ShutdownSpec makes no product-owned network requests, sends no telemetry, and writes no persistent local state. It does not inspect or transmit service logs, probe payloads, source paths, test names, or exception details outside the process.

The library returns lifecycle facts and exception type information to the calling test. A caller may choose to inspect application-owned exception messages or logs; those values are not copied into the default diagnostic report. The tested service remains arbitrary application code and is not sandboxed.
