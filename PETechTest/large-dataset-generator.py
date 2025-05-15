import csv
import random
import datetime
import uuid
import os

def generate_phone_number(allow_shortcode=False, allow_empty=False):
    """Generates a random phone number string, mimicking UK and some international formats."""
    if allow_empty and random.random() < 0.01: # 1% chance of empty caller_id
        return ""
    if allow_shortcode and random.random() < 0.02: # 2% chance of a shortcode for recipient
        return str(random.randint(10000, 999999))

    # Prefixes with rough probabilities
    prefixes = {
        "44": 0.85,  # UK
        "353": 0.05, # Ireland
        "1": 0.03,   # US/Canada
        "49": 0.02,  # Germany
        "33": 0.02,  # France
        "": 0.03     # No prefix / local
    }
    prefix_choice = random.choices(list(prefixes.keys()), weights=list(prefixes.values()), k=1)[0]

    if prefix_choice == "":
        # Generate a shorter, possibly local number
        return ''.join([str(random.randint(0, 9)) for _ in range(random.randint(7, 10))])
    else:
        # Generate a longer number for international/national
        return prefix_choice + ''.join([str(random.randint(0, 9)) for _ in range(random.randint(9, 12))])


def generate_date(start_year=2015, end_year=2024):
    """Generates a random date string in DD/MM/YYYY format."""
    start_date = datetime.date(start_year, 1, 1)
    end_date = datetime.date(end_year, 12, 31)
    time_between_dates = end_date - start_date
    days_between_dates = time_between_dates.days
    random_number_of_days = random.randrange(days_between_dates)
    random_date = start_date + datetime.timedelta(days=random_number_of_days)
    return random_date.strftime("%d/%m/%Y")

def generate_time():
    """Generates a random time string in HH:MM:SS format."""
    return f"{random.randint(0, 23):02}:{random.randint(0, 59):02}:{random.randint(0, 59):02}"

def generate_duration():
    """Generates a random call duration in seconds."""
    # Skew towards shorter calls, but allow for very long ones occasionally
    if random.random() < 0.7: # 70% of calls are shorter
        return random.randint(1, 600) # 1s to 10 mins
    elif random.random() < 0.95: # 25% are medium
        return random.randint(601, 3600) # 10 mins to 1 hour
    else: # 5% are very long
        return random.randint(3601, 7200) # 1 hour to 2 hours

def generate_cost():
    """Generates a random call cost."""
    if random.random() < 0.6:  # 60% chance of zero cost
        return "0"
    cost_val = round(random.uniform(0.001, 5.0), 3) # Costs between 0.001 and 5.000
    # Sometimes, make it an integer string if it's a whole number after rounding
    if cost_val == int(cost_val) and random.random() < 0.2: # e.g. some '0' costs were observed
        return str(int(cost_val))
    return f"{cost_val:.3f}" # Ensure three decimal places for consistency if not zero

def generate_reference():
    """Generates a 32-character hex string (UUID without hyphens)."""
    return uuid.uuid4().hex.upper()

def generate_currency():
    """Generates a currency code, predominantly GBP."""
    currencies = {
        "GBP": 0.95,
        "USD": 0.02,
        "EUR": 0.03
    }
    return random.choices(list(currencies.keys()), weights=list(currencies.values()), k=1)[0]

def generate_large_csv(filename="large_cdr_data.csv", target_gb=1.0):
    """Generates a large CSV file with CDR data."""
    headers = ["caller_id", "recipient", "call_date", "end_time", "duration", "cost", "reference", "currency"]
    target_bytes = int(target_gb * 1024 * 1024 * 1024)
    
    # Estimate average row size (can be refined by generating a few rows and measuring)
    # A sample row: "441215598896,448000096481,16/08/2016,14:21:33,43,0,C5DA9724701EEBBA95CA2CC5617BA93E4,GBP\n" is ~110-120 bytes
    avg_row_bytes_estimate = 125  # Including newline character
    num_rows_estimate = target_bytes // avg_row_bytes_estimate
    
    print(f"Target file size: {target_gb:.2f} GB ({target_bytes} bytes)")
    print(f"Estimated number of rows: {num_rows_estimate}")

    # Create the directory if it doesn't exist
    output_dir = "/tmp/data_generation" # Using /tmp for general compatibility
    os.makedirs(output_dir, exist_ok=True)
    filepath = os.path.join(output_dir, filename)

    try:
        with open(filepath, 'w', newline='', encoding='utf-8') as csvfile:
            writer = csv.writer(csvfile)
            writer.writerow(headers)
            
            current_bytes = len(','.join(headers)) + 2 # Estimate header size with newline

            for i in range(num_rows_estimate):
                row = [
                    generate_phone_number(allow_empty=True),
                    generate_phone_number(allow_shortcode=True),
                    generate_date(),
                    generate_time(),
                    str(generate_duration()),
                    generate_cost(),
                    generate_reference(),
                    generate_currency()
                ]
                writer.writerow(row)
                
                # Update current file size (approximation)
                current_bytes += len(','.join(map(str,row))) + 2 # +2 for newline (rough)
                
                if (i + 1) % 100000 == 0: # Print progress every 100,000 rows
                    print(f"Generated {i + 1} rows. Current estimated size: {current_bytes / (1024*1024):.2f} MB")
                
                # Check file size periodically to avoid overshooting too much
                if i % 50000 == 0 and i > 0: # Check every 50,000 rows after the first batch
                    if os.path.exists(filepath):
                        actual_size = os.path.getsize(filepath)
                        if actual_size >= target_bytes:
                            print(f"Target file size of ~{target_gb} GB reached. Stopping generation.")
                            print(f"Actual file size: {actual_size / (1024*1024*1024):.3f} GB")
                            break
            
        print(f"\nSuccessfully generated {filename} in {output_dir}")
        if os.path.exists(filepath):
             final_size_gb = os.path.getsize(filepath) / (1024*1024*1024)
             print(f"Final file size: {final_size_gb:.3f} GB")
             print(f"File saved at: {filepath}") # Provide the exact path
        else:
            print(f"Error: File {filepath} was not created.")

    except Exception as e:
        print(f"An error occurred: {e}")
        if os.path.exists(filepath): # Clean up partial file on error
            os.remove(filepath)
            print(f"Partially generated file {filepath} has been removed.")

if __name__ == "__main__":
    generate_large_csv(filename="large_cdr_data.csv", target_gb=1.0)

