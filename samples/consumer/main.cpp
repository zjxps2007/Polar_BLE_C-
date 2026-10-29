#include <polar/polar_sdk.hpp>
#include <iostream>
int main() {
    polar::sdk::context sdk;
    std::cout << "Polar Native ABI " << polar_sdk_abi_version() << " context "
              << sdk.native_handle() << '\n';
}
