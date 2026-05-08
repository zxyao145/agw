#import "RCTNativeAgwConfigFile.h"

@implementation RCTNativeAgwConfigFile

+ (NSString *)moduleName
{
    return @"NativeAgwConfigFile";
}

- (std::shared_ptr<facebook::react::TurboModule>)getTurboModule:
    (const facebook::react::ObjCTurboModule::InitParams &)params
{
    return std::make_shared<facebook::react::NativeAgwConfigFileSpecJSI>(params);
}

- (NSString * _Nullable)readConfig
{
    NSString *path = [self configFilePath];

    if (![[NSFileManager defaultManager] fileExistsAtPath:path]) {
        return nil;
    }

    NSError *error = nil;
    NSString *content = [NSString stringWithContentsOfFile:path
                                                  encoding:NSUTF8StringEncoding
                                                     error:&error];

    if (error != nil) {
        return nil;
    }

    return content;
}

- (NSString * _Nullable)writeConfig:(NSString *)value
{
    NSString *path = [self configFilePath];
    NSString *directory = [path stringByDeletingLastPathComponent];
    NSFileManager *fileManager = [NSFileManager defaultManager];
    NSError *directoryError = nil;

    [fileManager createDirectoryAtPath:directory
           withIntermediateDirectories:YES
                            attributes:nil
                                 error:&directoryError];

    if (directoryError != nil) {
        return directoryError.localizedDescription;
    }

    NSError *writeError = nil;
    [value writeToFile:path
            atomically:YES
              encoding:NSUTF8StringEncoding
                 error:&writeError];

    if (writeError != nil) {
        return writeError.localizedDescription;
    }

    return nil;
}

- (NSString * _Nullable)deleteConfig
{
    NSString *path = [self configFilePath];

    if ([[NSFileManager defaultManager] fileExistsAtPath:path]) {
        NSError *removeError = nil;
        [[NSFileManager defaultManager] removeItemAtPath:path error:&removeError];

        if (removeError != nil) {
            return removeError.localizedDescription;
        }
    }

    return nil;
}

- (NSString *)configFilePath
{
    NSArray<NSURL *> *urls = [[NSFileManager defaultManager] URLsForDirectory:NSApplicationSupportDirectory
                                                                    inDomains:NSUserDomainMask];
    NSURL *baseUrl = urls.firstObject;
    NSURL *directoryUrl = [baseUrl URLByAppendingPathComponent:@"Agw" isDirectory:YES];

    return [[directoryUrl URLByAppendingPathComponent:@"config.json"] path];
}

@end
