/** Shared polling cadence for all dashboard queries. Header sync indicator
 *  uses this + 1s slack to decide "fresh". Keep these three numbers linked. */
export const POLL_INTERVAL_MS = 5000;
export const POLL_STALE_TIME_MS = 4000;
export const POLL_FRESH_THRESHOLD_MS = 6000; // = interval + ~1s render slack
