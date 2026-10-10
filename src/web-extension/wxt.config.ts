import { defineConfig } from 'wxt';

// See https://wxt.dev/api/config.html
export default defineConfig({
    vite: () => ({
        server: {
            watch: {
                usePolling: true,
            },
        },
    }),
    dev: {
        server: {
            host: '127.0.0.1',
        },
    },
    manifest: {
        browser_specific_settings: {
            gecko: {
                id: "screen-time-tracker@local.invalid",
                strict_min_version: "140.0",
                data_collection_permissions: { required: ["browsingActivity", "websiteContent"] },
            },
        },
        name: "STT Web Extension",
        description: "Collect website usage data for your local app.",
        host_permissions: [
            "<all_urls>"
        ],
        permissions: ['contextMenus', 'storage', 'tabs'],
        action: {
            default_title: "STT Web Extension"
        },
    }
});
