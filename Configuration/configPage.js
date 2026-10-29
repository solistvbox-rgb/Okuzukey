define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select', 'emby-scroller'], function (BaseView, loading) {
    'use strict';

    var pluginUniqueId = '8f3b6a2e-8e1d-4a2b-9c3a-1f7e6d5c4b3a';

    function View(view, params) {
        BaseView.apply(this, arguments);
        var page = view;

        function byId(id) { return page.querySelector('#' + id); }

        function populateForm(config) {
            byId('EnableKomga').checked = config.EnableKomga;
            byId('KomgaBaseUrl').value = config.KomgaBaseUrl || '';
            byId('KomgaApiKey').value = config.KomgaApiKey || '';
            byId('KomgaTimeoutSeconds').value = config.KomgaTimeoutSeconds || 30;
            byId('KomgaMaxConcurrentRequests').value = config.KomgaMaxConcurrentRequests || 4;
            byId('KomgaLibraryPathOnEmby').value = config.KomgaLibraryPathOnEmby || '';
            byId('KomgaLibraryPathOnKomga').value = config.KomgaLibraryPathOnKomga || '';
            byId('LoggingLevel').value = config.LoggingLevel || 'Normal';
        }

        function applyFormToConfig(config) {
            config.EnableKomga = byId('EnableKomga').checked;
            config.KomgaBaseUrl = byId('KomgaBaseUrl').value;
            config.KomgaApiKey = byId('KomgaApiKey').value;

            var timeoutValue = parseInt(byId('KomgaTimeoutSeconds').value, 10);
            config.KomgaTimeoutSeconds = isNaN(timeoutValue) || timeoutValue <= 0 ? 30 : Math.min(timeoutValue, 300);

            var concurrencyValue = parseInt(byId('KomgaMaxConcurrentRequests').value, 10);
            config.KomgaMaxConcurrentRequests = isNaN(concurrencyValue) || concurrencyValue < 1 || concurrencyValue > 20 ? 4 : concurrencyValue;

            config.KomgaLibraryPathOnEmby = byId('KomgaLibraryPathOnEmby').value;
            config.KomgaLibraryPathOnKomga = byId('KomgaLibraryPathOnKomga').value;
            config.LoggingLevel = byId('LoggingLevel').value;

            return config;
        }

        function loadConfig() {
            loading.show();
            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                populateForm(config);
                loading.hide();
            }, function () {
                loading.hide();
            });
        }

        page.querySelector('#btnTestKomga').addEventListener('click', function () {
            var resultEl = byId('KomgaTestResult');
            var timeoutValue = parseInt(byId('KomgaTimeoutSeconds').value, 10);

            resultEl.style.color = '';
            resultEl.textContent = 'Testing...';
            loading.show();

            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('Okuzukey/KomgaTest'),
                data: JSON.stringify({
                    BaseUrl: byId('KomgaBaseUrl').value,
                    ApiKey: byId('KomgaApiKey').value,
                    TimeoutSeconds: isNaN(timeoutValue) ? 0 : timeoutValue
                }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (result) {
                loading.hide();
                resultEl.style.color = result.Success ? '#52b54b' : '#e5484d';
                resultEl.textContent = (result.Success ? '✓ ' : '✗ ') + result.Message;
            }, function () {
                loading.hide();
                resultEl.style.color = '#e5484d';
                resultEl.textContent = '✗ The test request to the Emby server failed (are you signed in as an administrator?).';
            });
        });

        page.querySelector('.okuzukeyConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();

            ApiClient.getPluginConfiguration(pluginUniqueId).then(function (config) {
                applyFormToConfig(config);
                ApiClient.updatePluginConfiguration(pluginUniqueId, config).then(function (result) {
                    Dashboard.processPluginConfigurationUpdateResult(result);
                }, function () {
                    loading.hide();
                    var resultEl = byId('KomgaTestResult');
                    resultEl.style.color = '#e5484d';
                    resultEl.textContent = '✗ The settings could not be saved. Please try again.';
                });
            }, function () {
                loading.hide();
            });

            return false;
        });

        this.onResume = function () {
            BaseView.prototype.onResume.apply(this, arguments);
            loadConfig();
        };

        loadConfig();
    }

    View.prototype = Object.create(BaseView.prototype);
    View.prototype.constructor = View;

    return View;
});
