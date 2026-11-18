/* Fixed-device Pro Max lighting transfers. Hardware admission belongs to the caller.
 * EE_PRO_MAX_TESTING builds only the portable parser and injected transport core.
 * EE_PRO_MAX_DEVICE_IO explicitly builds the unintegrated Linux device wrapper.
 */
#if defined(EE_PRO_MAX_DEVICE_IO) && !defined(_POSIX_C_SOURCE)
#define _POSIX_C_SOURCE 200809L
#endif
#include <stddef.h>
#include <stdint.h>
#include <string.h>

#if defined(EE_PRO_MAX_TESTING) && defined(EE_PRO_MAX_DEVICE_IO)
#error The test build must not contain device I/O
#endif

#if defined(_WIN32) && defined(EE_PRO_MAX_TESTING)
#define EE_EXPORT __declspec(dllexport)
#else
#define EE_EXPORT
#endif

#define EE_PM_ADDRESS 0x66u
#define EE_PM_LIMIT 16u

enum ee_pm_status {
    EE_PM_OK = 0,
    EE_PM_INVALID = -1,
    EE_PM_IO_ERROR = -2,
    EE_PM_INCOMPLETE = -3
};

struct ee_pm_command {
    uint16_t sysid;
    uint8_t board;
    uint8_t request[EE_PM_LIMIT];
    uint8_t request_length;
    uint8_t read_length;
};

struct ee_pm_message {
    uint16_t address;
    uint16_t flags;
    uint16_t length;
    uint8_t *data;
};

typedef int (*ee_pm_sink)(void *, struct ee_pm_message *, unsigned int);

static unsigned int port_count(uint16_t sysid, uint8_t board)
{
    if ((sysid == 0xed70u || sysid == 0xed71u) && board == 3u) return 18u;
    if ((sysid == 0xed5bu || sysid == 0xed5cu) && board == 2u) return 26u;
    if ((sysid == 0xed5du || sysid == 0xed5eu) && board == 1u) return 52u;
    return 0u;
}

static uint8_t checksum(const uint8_t *data, size_t length)
{
    unsigned int crc = 0u;
    size_t index;
    for (index = 0u; index < length; ++index) {
        unsigned int bit;
        crc ^= data[index];
        for (bit = 0u; bit < 8u; ++bit)
            crc = ((crc << 1u) ^ ((crc & 0x80u) ? 0x07u : 0u)) & 0xffu;
    }
    return (uint8_t)crc;
}

EE_EXPORT int ee_pm_validate(const struct ee_pm_command *command)
{
    unsigned int count;
    const uint8_t *request;
    size_t index;
    uint64_t mask = 0u;
    if (command == NULL) return EE_PM_INVALID;
    count = port_count(command->sysid, command->board);
    if (count == 0u || command->request_length == 0u || command->request_length > EE_PM_LIMIT)
        return EE_PM_INVALID;
    request = command->request;
    if (command->read_length != 0u) {
        if (command->request_length != 1u) return EE_PM_INVALID;
        if ((request[0] == 0x00u && command->read_length == 1u) ||
            (request[0] == 0x10u && command->read_length == 9u) ||
            (request[0] == 0x24u && command->read_length == 15u) ||
            (request[0] == 0x52u && command->read_length == 2u)) return EE_PM_OK;
        return EE_PM_INVALID;
    }
    if (request[0] == 0x10u && command->request_length == 2u)
        return request[1] < count ? EE_PM_OK : EE_PM_INVALID;
    if (request[0] == 0x20u && command->request_length == 13u) {
        if (request[1] >= count || request[2] != 8u || request[11] > 1u)
            return EE_PM_INVALID;
        return checksum(request + 1u, 11u) == request[12] ? EE_PM_OK : EE_PM_INVALID;
    }
    if (command->request_length != 16u) return EE_PM_INVALID;
    if (request[0] == 0x25u) {
        for (index = 1u; index < 8u; ++index)
            if (request[index] != 0u) return EE_PM_INVALID;
    } else if (request[0] == 0x00u) {
        if ((request[1] != 2u && request[1] != 3u) ||
            (request[2] != 0x19u && request[2] != 0x1au && request[2] != 0x1bu))
            return EE_PM_INVALID;
        for (index = 3u; index < 8u; ++index)
            if (request[index] != 0u) return EE_PM_INVALID;
    } else {
        return EE_PM_INVALID;
    }
    for (index = 8u; index < 15u; ++index) mask = (mask << 8u) | request[index];
    if (mask == 0u || (mask >> count) != 0u) return EE_PM_INVALID;
    return checksum(request + 1u, 14u) == request[15] ? EE_PM_OK : EE_PM_INVALID;
}

EE_EXPORT int ee_pm_transfer(const struct ee_pm_command *command, uint8_t *reply,
                            size_t capacity, ee_pm_sink sink, void *context, int *returned)
{
    uint8_t outgoing[EE_PM_LIMIT];
    uint8_t incoming[EE_PM_LIMIT] = {0};
    struct ee_pm_message messages[2];
    unsigned int count;
    int result;
    if (returned != NULL) *returned = 0;
    if (ee_pm_validate(command) != EE_PM_OK || sink == NULL) return EE_PM_INVALID;
    if (command->read_length != 0u && (reply == NULL || capacity < command->read_length))
        return EE_PM_INVALID;
    memcpy(outgoing, command->request, command->request_length);
    messages[0].address = EE_PM_ADDRESS;
    messages[0].flags = 0u;
    messages[0].length = command->request_length;
    messages[0].data = outgoing;
    count = command->read_length == 0u ? 1u : 2u;
    if (count == 2u) {
        messages[1].address = EE_PM_ADDRESS;
        messages[1].flags = 1u;
        messages[1].length = command->read_length;
        messages[1].data = incoming;
    }
    result = sink(context, messages, count);
    if (returned != NULL) *returned = result;
    if (result < 0) return EE_PM_IO_ERROR;
    if ((unsigned int)result != count) return EE_PM_INCOMPLETE;
    if (command->read_length != 0u) memcpy(reply, incoming, command->read_length);
    return EE_PM_OK;
}

static int hex_digit(char value)
{
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

static size_t bounded_length(const char *value, size_t limit)
{
    size_t length = 0u;
    while (length < limit && value[length] != '\0') ++length;
    return length;
}

static int parse_hex(const char *value, uint8_t *bytes, size_t length)
{
    size_t index;
    if (bounded_length(value, length * 2u + 1u) != length * 2u) return EE_PM_INVALID;
    for (index = 0u; index < length; ++index) {
        int high = hex_digit(value[index * 2u]);
        int low = hex_digit(value[index * 2u + 1u]);
        if (high < 0 || low < 0) return EE_PM_INVALID;
        bytes[index] = (uint8_t)((unsigned int)high * 16u + (unsigned int)low);
    }
    return EE_PM_OK;
}

EE_EXPORT int ee_pm_parse(int argc, const char *const *argv, struct ee_pm_command *output)
{
    struct ee_pm_command command = {0};
    uint8_t identity[2];
    int index;
    if (output == NULL || argv == NULL || (argc != 4 && argc != 5)) return EE_PM_INVALID;
    for (index = 0; index < argc; ++index)
        if (argv[index] == NULL) return EE_PM_INVALID;
    if (parse_hex(argv[1], identity, 2u) != EE_PM_OK || bounded_length(argv[2], 2u) != 1u ||
        argv[2][0] < '1' || argv[2][0] > '3') return EE_PM_INVALID;
    command.sysid = (uint16_t)((unsigned int)identity[0] * 256u + identity[1]);
    command.board = (uint8_t)(argv[2][0] - '0');
    if (strcmp(argv[3], "packet") == 0) {
        size_t length;
        if (argc != 5) return EE_PM_INVALID;
        length = bounded_length(argv[4], 33u);
        if (length != 26u && length != 32u) return EE_PM_INVALID;
        command.request_length = (uint8_t)(length / 2u);
        if (parse_hex(argv[4], command.request, command.request_length) != EE_PM_OK)
            return EE_PM_INVALID;
    } else if (strcmp(argv[3], "select") == 0) {
        unsigned int port = 0u;
        size_t length;
        size_t digit;
        if (argc != 5) return EE_PM_INVALID;
        length = bounded_length(argv[4], 3u);
        if (length == 0u || length > 2u || argv[4][0] == '0') return EE_PM_INVALID;
        for (digit = 0u; digit < length; ++digit) {
            if (argv[4][digit] < '0' || argv[4][digit] > '9') return EE_PM_INVALID;
            port = port * 10u + (unsigned int)(argv[4][digit] - '0');
        }
        if (port == 0u || port > port_count(command.sysid, command.board)) return EE_PM_INVALID;
        command.request[0] = 0x10u;
        command.request[1] = (uint8_t)(port - 1u);
        command.request_length = 2u;
    } else {
        if (argc != 4) return EE_PM_INVALID;
        command.request_length = 1u;
        if (strcmp(argv[3], "snapshot") == 0) {
            command.request[0] = 0x10u;
            command.read_length = 9u;
        } else if (strcmp(argv[3], "acceptance") == 0) {
            command.request[0] = 0x52u;
            command.read_length = 2u;
        } else if (strcmp(argv[3], "masked-acceptance") == 0) {
            command.request[0] = 0x24u;
            command.read_length = 15u;
        } else if (strcmp(argv[3], "mode-observation") == 0) {
            command.request[0] = 0x00u;
            command.read_length = 1u;
        } else {
            return EE_PM_INVALID;
        }
    }
    if (ee_pm_validate(&command) != EE_PM_OK) return EE_PM_INVALID;
    *output = command;
    return EE_PM_OK;
}

#if defined(EE_PRO_MAX_DEVICE_IO)
#if !defined(__linux__)
#error The device wrapper requires target Linux headers
#endif
#include <errno.h>
#include <fcntl.h>
#include <linux/i2c-dev.h>
#include <linux/i2c.h>
#include <stdio.h>
#include <sys/ioctl.h>
#include <sys/stat.h>
#include <unistd.h>

static int linux_transfer(void *context, struct ee_pm_message *messages, unsigned int count)
{
    struct i2c_msg native[2];
    struct i2c_rdwr_ioctl_data transfer;
    unsigned int index;
    int result;
    for (index = 0u; index < count; ++index) {
        native[index].addr = messages[index].address;
        native[index].flags = messages[index].flags;
        native[index].len = messages[index].length;
        native[index].buf = messages[index].data;
    }
    transfer.msgs = native;
    transfer.nmsgs = count;
    result = ioctl(*(int *)context, I2C_RDWR, &transfer);
    return result < 0 ? -errno : result;
}

int main(int argc, char **argv)
{
    struct ee_pm_command command;
    struct stat device;
    uint8_t reply[EE_PM_LIMIT];
    int descriptor;
    int result;
    int returned;
    unsigned int index;
    if (ee_pm_parse(argc, (const char *const *)argv, &command) != EE_PM_OK) {
        fputs("Invalid Pro Max lighting operation.\n", stderr);
        return 2;
    }
    descriptor = open("/dev/i2c-104", O_RDWR | O_CLOEXEC | O_NOFOLLOW);
    if (descriptor < 0) {
        fputs("Cannot open the Pro Max lighting transport.\n", stderr);
        return 3;
    }
    if (fstat(descriptor, &device) != 0 || !S_ISCHR(device.st_mode)) {
        close(descriptor);
        fputs("Invalid Pro Max lighting transport.\n", stderr);
        return 3;
    }
    result = ee_pm_transfer(&command, reply, sizeof(reply), linux_transfer, &descriptor, &returned);
    close(descriptor);
    if (result != EE_PM_OK) {
        fprintf(stderr, "Lighting transfer could not be confirmed (%d).\n", returned);
        return 4;
    }
    for (index = 0u; index < command.read_length; ++index) printf("%02x", (unsigned int)reply[index]);
    putchar('\n');
    return 0;
}
#endif
