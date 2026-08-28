#!/bin/sh
# Runs before nginx starts. Two jobs, and the split between them is the point.
#
# The API key goes into nginx's own configuration, where the proxy can add it
# to each upstream request. It is never written anywhere under the document
# root, so there is no URL that serves it and nothing in the page to copy.
#
# Everything the browser is allowed to know goes into config.js, which is now
# only a set of paths on this same origin.
set -eu

CONFIG_FILE=/usr/share/nginx/html/config.js
NGINX_CONF=/etc/nginx/conf.d/default.conf

API_KEY="${WEB_API_KEY:-}"

if [ -z "$API_KEY" ]; then
    # Refused rather than started, because a proxy with no key produces a
    # dashboard where every request fails with 401 and nothing says why.
    echo "incidentiq: WEB_API_KEY is not set; the proxy would authenticate nothing." >&2
    exit 1
fi

# The placeholder appears exactly once, in the map block at the top.
sed -i "s|__INCIDENTIQ_API_KEY__|${API_KEY}|" "$NGINX_CONF"

cat > "$CONFIG_FILE" <<JS
window.__INCIDENTIQ_CONFIG__ = {
  apiBaseUrl: "",
  ingestionBaseUrl: "/ingest",
};
JS

echo "incidentiq: proxying /api, /hubs and /ingest with a server-side key"
