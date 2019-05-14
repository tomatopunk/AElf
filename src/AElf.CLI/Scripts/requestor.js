(function () {
    _requestor.isConnected = function () {
        try {
            return this.isConnected();
        } catch (e) {
            return false;
        }
    }
})();