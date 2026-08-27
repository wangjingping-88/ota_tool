/**
 * @file partition_patch_verify.cpp
 * @brief Headless verifier for UCCHIP partition BSDiff/LZzip packages.
 * @version 1.0.0
 * @date 2026-08-27
 *
 * The patch decoding rules are derived from the UCCHIP bspatch.cpp source
 * originally published by chenkang in 2020 under the 2-clause BSD license.
 * The complete attribution is kept in THIRD_PARTY_NOTICES.md.
 */

#include <algorithm>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
constexpr std::size_t PATCH_HEADER_SIZE = 16;
constexpr std::size_t BOOTLOADER_SIZE = 28 * 1024;
constexpr std::uint64_t FILE_SIZE_MAX = 32ULL * 1024ULL * 1024ULL;
constexpr std::uint8_t ZIP_FLAG_MAX = 5;

class verify_error : public std::runtime_error
{
public:
    explicit verify_error(const std::string &message)
        : std::runtime_error(message)
    {
    }
};

struct patch_header_t
{
    std::uint8_t split_sum;
    std::uint32_t split_len;
    std::uint8_t split_num;
    std::uint32_t new_file_len;
    std::uint32_t start_addr;
    std::uint16_t crc;
    std::uint8_t end_flag;
};

enum class patch_stream_state_e
{
    control,
    old_index,
    diff_length,
    extra_length,
    data,
};

/**
 * @brief Read one complete binary file.
 * @param path Input path, owned by the caller and valid during the call.
 * @return File contents owned by the returned vector.
 */
std::vector<std::uint8_t> read_file(const std::filesystem::path &path)
{
    std::ifstream stream(path, std::ios::binary | std::ios::ate);
    if (!stream)
    {
        throw verify_error("cannot open input file");
    }

    const std::streamoff length = stream.tellg();
    if ((length <= 0) || (static_cast<std::uint64_t>(length) > FILE_SIZE_MAX))
    {
        throw verify_error("input file size is outside the supported range");
    }

    std::vector<std::uint8_t> data(static_cast<std::size_t>(length));
    stream.seekg(0, std::ios::beg);
    if (!stream.read(reinterpret_cast<char *>(data.data()), length))
    {
        throw verify_error("cannot read input file");
    }
    return data;
}

std::uint16_t read_be16(const std::uint8_t *data)
{
    return static_cast<std::uint16_t>(
        (static_cast<std::uint16_t>(data[0]) << 8U) |
        static_cast<std::uint16_t>(data[1]));
}

std::uint32_t read_be24(const std::uint8_t *data)
{
    return (static_cast<std::uint32_t>(data[0]) << 16U) |
           (static_cast<std::uint32_t>(data[1]) << 8U) |
           static_cast<std::uint32_t>(data[2]);
}

/**
 * @brief Parse and validate a 16-byte partition patch header.
 * @param data Input bytes, owned by the caller and valid during the call.
 * @param remaining Number of bytes available from data.
 * @return Parsed header value.
 */
patch_header_t parse_header(const std::uint8_t *data, std::size_t remaining)
{
    if (remaining < PATCH_HEADER_SIZE)
    {
        throw verify_error("truncated patch header");
    }

    patch_header_t header{};
    header.split_sum = data[0];
    header.split_len = read_be24(data + 1);
    header.split_num = data[4];
    header.new_file_len = read_be24(data + 5);
    header.start_addr = read_be24(data + 8);
    header.crc = read_be16(data + 11);
    header.end_flag = data[13];

    if ((0 == header.split_sum) ||
        (0 == header.split_num) ||
        (header.split_num > header.split_sum))
    {
        throw verify_error("invalid patch block numbering");
    }
    if ((header.split_len < (PATCH_HEADER_SIZE + 2)) ||
        (header.split_len > remaining))
    {
        throw verify_error("invalid patch block length");
    }
    if (0 == header.new_file_len)
    {
        throw verify_error("patch block has an empty output range");
    }
    if (header.end_flag > 1)
    {
        throw verify_error("invalid patch end flag");
    }
    return header;
}

/**
 * @brief Calculate CRC-16/USB for a restored partition.
 * @param data Input bytes, owned by the caller and valid during the call.
 * @param length Input length in bytes.
 * @return CRC-16/USB value.
 */
std::uint16_t crc_16_usb(const std::uint8_t *data, std::size_t length)
{
    std::uint16_t crc = 0xFFFFU;
    for (std::size_t index = 0; index < length; index++)
    {
        crc ^= data[index];
        for (unsigned int bit = 0; bit < 8; bit++)
        {
            if (0U != (crc & 1U))
            {
                crc = static_cast<std::uint16_t>((crc >> 1U) ^ 0xA001U);
            }
            else
            {
                crc = static_cast<std::uint16_t>(crc >> 1U);
            }
        }
    }
    return static_cast<std::uint16_t>(crc ^ 0xFFFFU);
}

class patch_stream_decoder_t
{
public:
    patch_stream_decoder_t(
        const std::uint8_t *old_data,
        std::size_t old_length,
        std::size_t expected_length)
        : old_data_(old_data),
          old_length_(old_length),
          expected_length_(expected_length)
    {
        output_.reserve(expected_length);
    }

    /**
     * @brief Consume one byte from the decompressed BSDiff stream.
     * @param data Input byte.
     * @return Nothing; malformed input raises verify_error.
     */
    void consume(std::uint8_t data)
    {
        switch (state_)
        {
        case patch_stream_state_e::control:
            consume_control(data);
            break;
        case patch_stream_state_e::old_index:
            consume_old_index(data);
            break;
        case patch_stream_state_e::diff_length:
            consume_diff_length(data);
            break;
        case patch_stream_state_e::extra_length:
            consume_extra_length(data);
            break;
        case patch_stream_state_e::data:
            consume_patch_data(data);
            break;
        default:
            throw verify_error("invalid BSDiff decoder state");
        }
    }

    /**
     * @brief Finish decoding and return the restored block.
     * @return Restored bytes owned by the returned vector.
     */
    std::vector<std::uint8_t> finish(void)
    {
        if (patch_stream_state_e::control != state_)
        {
            throw verify_error("truncated BSDiff control stream");
        }
        if (output_.size() != expected_length_)
        {
            throw verify_error("restored block length does not match its header");
        }
        return std::move(output_);
    }

private:
    void consume_control(std::uint8_t data)
    {
        control_size_ = static_cast<unsigned int>((data >> 6U) + 1U);
        diff_size_ = static_cast<unsigned int>((data >> 3U) & 0x07U);
        extra_size_ = static_cast<unsigned int>(data & 0x07U);
        if ((control_size_ > 4U) || (diff_size_ > 4U) || (extra_size_ > 4U))
        {
            throw verify_error("unsupported BSDiff integer width");
        }
        field_bytes_ = 0;
        old_index_ = 0;
        diff_remaining_ = 0;
        extra_remaining_ = 0;
        state_ = patch_stream_state_e::old_index;
    }

    void consume_old_index(std::uint8_t data)
    {
        old_index_ |= static_cast<std::uint64_t>(data) << (field_bytes_ * 8U);
        field_bytes_++;
        if (field_bytes_ == control_size_)
        {
            field_bytes_ = 0;
            if (0U != diff_size_)
            {
                state_ = patch_stream_state_e::diff_length;
            }
            else if (0U != extra_size_)
            {
                state_ = patch_stream_state_e::extra_length;
            }
            else
            {
                state_ = patch_stream_state_e::control;
            }
        }
    }

    void consume_diff_length(std::uint8_t data)
    {
        diff_remaining_ |= static_cast<std::uint64_t>(data) << (field_bytes_ * 8U);
        field_bytes_++;
        if (field_bytes_ == diff_size_)
        {
            validate_run_length(diff_remaining_);
            field_bytes_ = 0;
            if (0U != extra_size_)
            {
                state_ = patch_stream_state_e::extra_length;
            }
            else if (0U != diff_remaining_)
            {
                state_ = patch_stream_state_e::data;
            }
            else
            {
                state_ = patch_stream_state_e::control;
            }
        }
    }

    void consume_extra_length(std::uint8_t data)
    {
        extra_remaining_ |= static_cast<std::uint64_t>(data) << (field_bytes_ * 8U);
        field_bytes_++;
        if (field_bytes_ == extra_size_)
        {
            validate_run_length(extra_remaining_);
            field_bytes_ = 0;
            if ((0U != diff_remaining_) || (0U != extra_remaining_))
            {
                state_ = patch_stream_state_e::data;
            }
            else
            {
                state_ = patch_stream_state_e::control;
            }
        }
    }

    void consume_patch_data(std::uint8_t data)
    {
        if (0U != diff_remaining_)
        {
            if (old_index_ >= old_length_)
            {
                throw verify_error("BSDiff old-image index is outside the source block");
            }
            append(static_cast<std::uint8_t>(old_data_[old_index_] + data));
            old_index_++;
            diff_remaining_--;
        }
        else if (0U != extra_remaining_)
        {
            append(data);
            extra_remaining_--;
        }
        else
        {
            throw verify_error("BSDiff stream contains unexpected data");
        }

        if ((0U == diff_remaining_) && (0U == extra_remaining_))
        {
            state_ = patch_stream_state_e::control;
        }
    }

    void validate_run_length(std::uint64_t length) const
    {
        if (length > expected_length_)
        {
            throw verify_error("BSDiff run length exceeds the restored block");
        }
    }

    void append(std::uint8_t data)
    {
        if (output_.size() >= expected_length_)
        {
            throw verify_error("BSDiff stream produces too much output");
        }
        output_.push_back(data);
    }

    const std::uint8_t *old_data_;
    std::size_t old_length_;
    std::size_t expected_length_;
    patch_stream_state_e state_ = patch_stream_state_e::control;
    unsigned int control_size_ = 0;
    unsigned int diff_size_ = 0;
    unsigned int extra_size_ = 0;
    unsigned int field_bytes_ = 0;
    std::uint64_t old_index_ = 0;
    std::uint64_t diff_remaining_ = 0;
    std::uint64_t extra_remaining_ = 0;
    std::vector<std::uint8_t> output_;
};

/**
 * @brief Restore one partition block from its LZzip stream.
 * @param old_data Source block, owned by the caller and valid during the call.
 * @param old_length Source block length in bytes.
 * @param data Compressed block payload after the 16-byte header.
 * @param length Compressed payload length in bytes.
 * @param expected_length Required restored block length in bytes.
 * @return Restored partition bytes.
 */
std::vector<std::uint8_t> restore_block(
    const std::uint8_t *old_data,
    std::size_t old_length,
    const std::uint8_t *data,
    std::size_t length,
    std::size_t expected_length)
{
    if (length < 2)
    {
        throw verify_error("truncated LZzip stream");
    }

    const std::uint8_t zip_flag = data[0];
    if (zip_flag > ZIP_FLAG_MAX)
    {
        throw verify_error("unsupported LZzip window flag");
    }

    const std::size_t window_length = static_cast<std::size_t>(1U) << (10U + zip_flag);
    const std::uint64_t emitted_limit =
        static_cast<std::uint64_t>(expected_length) * 6ULL + 4096ULL;
    std::vector<std::uint8_t> window(window_length, 0);
    patch_stream_decoder_t decoder(old_data, old_length, expected_length);
    std::size_t write_pos = 0;
    std::uint64_t emitted = 0;

    const auto emit = [&](std::uint8_t value)
    {
        if (emitted >= emitted_limit)
        {
            throw verify_error("LZzip stream exceeds its safe expansion limit");
        }
        window[write_pos] = value;
        write_pos = (write_pos + 1U) % window_length;
        emitted++;
        decoder.consume(value);
    };

    std::size_t input_pos = 1;
    emit(data[input_pos++]);
    while (input_pos < length)
    {
        std::uint32_t control = data[input_pos++];
        if (0x0FU == (control >> 4U))
        {
            const std::size_t literal_length = (control & 0x0FU) + 1U;
            if (literal_length > (length - input_pos))
            {
                throw verify_error("truncated LZzip literal");
            }
            for (std::size_t index = 0; index < literal_length; index++)
            {
                emit(data[input_pos++]);
            }
            continue;
        }

        unsigned int right_length = 0;
        unsigned int match_compensation = 0;
        std::uint32_t index_mask = 0;
        std::uint32_t match_mask = 0;
        if (0U == (control >> 7U))
        {
            right_length = 1U;
            match_compensation = 2U;
            index_mask = 0x3FU << 1U;
            match_mask = 0x01U;
        }
        else if (2U == (control >> 6U))
        {
            if (input_pos >= length)
            {
                throw verify_error("truncated two-byte LZzip reference");
            }
            control = ((control & 0x3FU) << 8U) | data[input_pos++];
            right_length = 4U - (zip_flag / 2U);
            match_compensation = 3U;
            index_mask = ((1U << (10U + (zip_flag / 2U))) - 1U) << right_length;
            match_mask = (1U << right_length) - 1U;
        }
        else if (6U == (control >> 5U))
        {
            if ((length - input_pos) < 2U)
            {
                throw verify_error("truncated three-byte LZzip reference");
            }
            control = ((control & 0x1FU) << 16U) |
                      (static_cast<std::uint32_t>(data[input_pos]) << 8U) |
                      data[input_pos + 1U];
            input_pos += 2U;
            right_length = 10U - zip_flag;
            match_compensation = 4U;
            index_mask = ((1U << (10U + zip_flag)) - 1U) << right_length;
            match_mask = (1U << right_length) - 1U;
        }
        else
        {
            if ((length - input_pos) < 3U)
            {
                throw verify_error("truncated four-byte LZzip reference");
            }
            control = ((control & 0x0FU) << 24U) |
                      (static_cast<std::uint32_t>(data[input_pos]) << 16U) |
                      (static_cast<std::uint32_t>(data[input_pos + 1U]) << 8U) |
                      data[input_pos + 2U];
            input_pos += 3U;
            right_length = 18U - zip_flag;
            match_compensation = 5U;
            index_mask = ((1U << (10U + zip_flag)) - 1U) << right_length;
            match_mask = (1U << right_length) - 1U;
        }

        const std::size_t index = (control & index_mask) >> right_length;
        const std::size_t match_length = (control & match_mask) + match_compensation;
        const std::size_t available = static_cast<std::size_t>(
            std::min<std::uint64_t>(emitted, window_length));
        if (index >= available)
        {
            throw verify_error("LZzip reference points outside its history window");
        }

        std::size_t read_pos =
            (write_pos + window_length - index - 1U) % window_length;
        for (std::size_t count = 0; count < match_length; count++)
        {
            const std::uint8_t value = window[read_pos];
            read_pos = (read_pos + 1U) % window_length;
            emit(value);
        }
    }

    return decoder.finish();
}

/**
 * @brief Verify every block and compare the reconstructed image.
 * @param old_image Source image, owned by the caller and valid during the call.
 * @param patch Complete patch package, owned by the caller and valid during the call.
 * @param expected_image Expected target image, owned by the caller and valid during the call.
 * @return Number of verified blocks.
 */
std::size_t verify_patch(
    const std::vector<std::uint8_t> &old_image,
    const std::vector<std::uint8_t> &patch,
    const std::vector<std::uint8_t> &expected_image)
{
    std::vector<std::uint8_t> restored(expected_image.size(), 0xFFU);
    const std::size_t copied_length = std::min(old_image.size(), restored.size());
    std::copy_n(old_image.begin(), copied_length, restored.begin());
    std::vector<bool> block_numbers(256, false);
    std::vector<bool> covered(expected_image.size(), false);
    std::size_t patch_pos = 0;
    std::size_t block_count = 0;
    std::uint8_t split_sum = 0;
    bool updates_first_block = false;

    while (patch_pos < patch.size())
    {
        const patch_header_t header = parse_header(
            patch.data() + patch_pos,
            patch.size() - patch_pos);
        if (0U == block_count)
        {
            split_sum = header.split_sum;
        }
        else if (header.split_sum != split_sum)
        {
            throw verify_error("patch blocks disagree on their total count");
        }
        if (block_numbers[header.split_num])
        {
            throw verify_error("patch contains a duplicate block number");
        }
        block_numbers[header.split_num] = true;

        const std::uint64_t block_end =
            static_cast<std::uint64_t>(header.start_addr) + header.new_file_len;
        if (block_end > expected_image.size())
        {
            throw verify_error("patch output range exceeds the expected image");
        }
        for (std::size_t index = header.start_addr;
             index < static_cast<std::size_t>(block_end);
             index++)
        {
            if (covered[index])
            {
                throw verify_error("patch output ranges overlap");
            }
            covered[index] = true;
        }

        const std::size_t old_length =
            header.start_addr < old_image.size()
                ? old_image.size() - header.start_addr
                : 0U;
        const std::uint8_t *old_data =
            0U != old_length ? old_image.data() + header.start_addr : nullptr;
        const std::size_t payload_pos = patch_pos + PATCH_HEADER_SIZE;
        const std::size_t payload_length = header.split_len - PATCH_HEADER_SIZE;
        const std::vector<std::uint8_t> block = restore_block(
            old_data,
            old_length,
            patch.data() + payload_pos,
            payload_length,
            header.new_file_len);
        if (crc_16_usb(block.data(), block.size()) != header.crc)
        {
            throw verify_error("restored block CRC-16/USB does not match its header");
        }
        std::copy(block.begin(), block.end(), restored.begin() + header.start_addr);
        updates_first_block = updates_first_block || (0U == header.start_addr);

        patch_pos += header.split_len;
        block_count++;
        const bool is_last_block = patch_pos == patch.size();
        if ((is_last_block && (1U != header.end_flag)) ||
            (!is_last_block && (0U != header.end_flag)))
        {
            throw verify_error("patch end flag does not match the physical block order");
        }
    }

    if (0U == block_count)
    {
        throw verify_error("patch contains no blocks");
    }

    std::vector<std::uint8_t> normalized_expected = expected_image;
    if (!updates_first_block)
    {
        const std::size_t preserved_length = std::min(
            BOOTLOADER_SIZE,
            std::min(old_image.size(), normalized_expected.size()));
        std::copy_n(old_image.begin(), preserved_length, normalized_expected.begin());
    }

    const auto mismatch = std::mismatch(
        restored.begin(),
        restored.end(),
        normalized_expected.begin());
    if (mismatch.first != restored.end())
    {
        const std::size_t offset =
            static_cast<std::size_t>(mismatch.first - restored.begin());
        throw verify_error("restored image mismatch at offset " + std::to_string(offset));
    }
    return block_count;
}
} // namespace

int wmain(int argc, wchar_t *argv[])
{
    if (4 != argc)
    {
        std::cerr << "Usage: partition_patch_verify.exe <old> <patch> <expected>\n";
        return 1;
    }

    try
    {
        const std::vector<std::uint8_t> old_image = read_file(argv[1]);
        const std::vector<std::uint8_t> patch = read_file(argv[2]);
        const std::vector<std::uint8_t> expected_image = read_file(argv[3]);
        const std::size_t block_count = verify_patch(
            old_image,
            patch,
            expected_image);
        std::cout << "OK blocks=" << block_count
                  << " restored_bytes=" << expected_image.size() << "\n";
        return 0;
    }
    catch (const std::exception &exception)
    {
        std::cerr << "ERROR " << exception.what() << "\n";
        return 3;
    }
}
