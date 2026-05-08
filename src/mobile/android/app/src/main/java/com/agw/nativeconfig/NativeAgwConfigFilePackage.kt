package com.agw.nativeconfig

import com.facebook.react.BaseReactPackage
import com.facebook.react.bridge.NativeModule
import com.facebook.react.bridge.ReactApplicationContext
import com.facebook.react.module.model.ReactModuleInfo
import com.facebook.react.module.model.ReactModuleInfoProvider

class NativeAgwConfigFilePackage : BaseReactPackage() {
    override fun getModule(
        name: String,
        reactContext: ReactApplicationContext
    ): NativeModule? =
        if (name == NativeAgwConfigFileModule.NAME) {
            NativeAgwConfigFileModule(reactContext)
        } else {
            null
        }

    override fun getReactModuleInfoProvider(): ReactModuleInfoProvider =
        ReactModuleInfoProvider {
            mapOf(
                NativeAgwConfigFileModule.NAME to ReactModuleInfo(
                    name = NativeAgwConfigFileModule.NAME,
                    className = NativeAgwConfigFileModule.NAME,
                    canOverrideExistingModule = false,
                    needsEagerInit = false,
                    isCxxModule = false,
                    isTurboModule = true
                )
            )
        }
}
